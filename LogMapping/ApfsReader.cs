// ApfsReader.cs — Read-only APFS file-listing for Windows
// Supports: single/multi-volume containers, uncrypted volumes
// Does not support: FileVault encryption, APFS-compressed file size lookup (shows 0)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LogMapping
{
    internal sealed class ApfsVolumeInfo
    {
        public ulong PhysBlock    { get; init; }
        public ulong OmapPhys     { get; init; }
        public ulong RootTreeOid  { get; init; }
        public string Name        { get; init; } = "";
        public ulong UsedMB       { get; init; }
    }

    internal sealed class ApfsReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly long   _partitionOffset; // 물리 디스크에서 파티션 시작 바이트 오프셋
        private uint _blockSize = 4096;

        // Magic values
        private const uint NXSB = 0x4253584E;
        private const uint APSB = 0x42535041;

        // Object type (low 16 bits of o_type)
        private const ushort TYPE_CHECKPOINT_MAP = 0x000C;
        private const ushort TYPE_OMAP           = 0x000B;
        private const ushort TYPE_BTREE_NODE     = 0x0004;

        // FS record types (high 4 bits of obj_id_and_type key field)
        private const ulong REC_INODE   = 3;
        private const ulong REC_DIR_REC = 9;

        // B-tree node flags
        private const ushort BTNODE_ROOT = 0x0001;
        private const ushort BTNODE_LEAF = 0x0002;

        // Root directory inode number
        private const ulong ROOT_INO = 2;

        public ApfsReader(Stream stream, long partitionOffset = 0)
        {
            _stream          = stream;
            _partitionOffset = partitionOffset;
        }

        // 물리 디스크 스트림에서 파티션 오프셋을 지정해 NXSB 여부 확인
        // 섹터 정렬 읽기: partitionOffset(= 섹터 경계)부터 512바이트 읽어서 내부 offset 32 확인
        public static bool Detect(Stream stream, long partitionOffset = 0)
        {
            try
            {
                stream.Seek(partitionOffset, SeekOrigin.Begin);
                var b = new byte[512];
                int n = stream.Read(b, 0, 512);
                return n >= 36 && BitConverter.ToUInt32(b, 32) == NXSB;
            }
            catch { return false; }
        }

        // ── block I/O ────────────────────────────────────────────────────────

        private byte[] ReadBlock(ulong n)
        {
            var buf = new byte[_blockSize];
            long pos = _partitionOffset + (long)(n * _blockSize);
            // Position 할당 (물리 드라이브에서 Seek 대신 Position 사용)
            _stream.Position = pos;
            int done = 0;
            while (done < (int)_blockSize)
            {
                int r = _stream.Read(buf, done, (int)_blockSize - done);
                if (r == 0) break;
                done += r;
            }
            return buf;
        }

        private static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
        private static uint   U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        private static ulong  U64(byte[] b, int o) => BitConverter.ToUInt64(b, o);

        // ── container: find volumes ───────────────────────────────────────────

        public List<ApfsVolumeInfo> FindVolumes()
        {
            var result = new List<ApfsVolumeInfo>();

            // Block 0 = container superblock (nx_superblock_t)
            var sb = ReadBlock(0);
            if (U32(sb, 32) != NXSB) return result;

            _blockSize = U32(sb, 36);
            if (_blockSize < 512 || _blockSize > 65536) return result;

            sb = ReadBlock(0); // re-read with correct block size

            // nx_superblock_t offsets:
            //  104: nx_xp_desc_blocks (4) — total blocks in descriptor area
            //  112: nx_xp_desc_base   (8) — first block of descriptor area
            //  136: nx_xp_desc_index  (4) — start of current checkpoint
            //  140: nx_xp_desc_len    (4) — length of current checkpoint
            //  180: nx_max_file_systems (4)
            //  184: nx_fs_oid[0..99]  (8 each)

            uint  xpDescBlocks = U32(sb, 104);
            uint  xpDataBlocks = U32(sb, 108);
            ulong xpDescBase   = (ulong)BitConverter.ToInt64(sb, 112);
            ulong xpDataBase   = (ulong)BitConverter.ToInt64(sb, 120);
            uint  xpDescIndex  = U32(sb, 136);
            uint  xpDescLen    = U32(sb, 140);
            uint  xpDataIndex  = U32(sb, 144);
            uint  xpDataLen    = U32(sb, 148);

            // ── 현재 체크포인트의 NX 수퍼블록 복사본을 찾아 activeSb로 사용 ─────────
            // block 0은 구버전일 수 있음. 체크포인트 desc 영역에서 type=NXSB(magic=NXSB)
            // 블록 중 xid가 가장 높은 것이 현재 유효한 수퍼블록.
            var activeSb = sb;
            if (xpDescBlocks > 0 && xpDescLen > 0)
            {
                ulong bestXid = U64(sb, 16); // o_xid는 offset 16 (offset 8은 o_oid)
                for (uint i = 0; i < xpDescLen; i++)
                {
                    ulong blkNum = xpDescBase + (xpDescIndex + i) % xpDescBlocks;
                    byte[] blk;
                    try { blk = ReadBlock(blkNum); } catch { continue; }
                    if (blk.Length < 40) continue;
                    if (U32(blk, 32) != NXSB) continue;
                    ulong xid = U64(blk, 16); // o_xid at offset 16
                    if (xid >= bestXid) { bestXid = xid; activeSb = blk; }
                }
            }

            // activeSb에서 데이터 인덱스 갱신 — block 0은 stale일 수 있음
            if (!ReferenceEquals(activeSb, sb))
            {
                xpDataIndex = U32(activeSb, 144);
                xpDataLen   = U32(activeSb, 148);
            }

            // ── 방법 1: 체크포인트 디스크립터 전체(tot 블록)를 스캔 ──────────────────
            // APFS는 변경된 객체만 현재 체크포인트에 기록하므로,
            // 볼륨 수퍼블록처럼 오래된 객체는 이전 체크포인트 맵에만 있을 수 있음.
            // 모든 체크포인트 맵 블록을 스캔해 OID→paddr 최신 매핑을 수집.
            var ephemMap = new Dictionary<ulong, ulong>(); // OID → paddr (나중 값이 최신)

            if (xpDescBlocks > 0)
            {
                uint scanBlocks = Math.Min(xpDescBlocks, 4096u);
                for (uint i = 0; i < scanBlocks; i++)
                {
                    ulong blkNum = xpDescBase + i;
                    byte[] blk;
                    try { blk = ReadBlock(blkNum); } catch { continue; }

                    ushort objType = (ushort)(U32(blk, 24) & 0xFFFF);
                    if (objType != TYPE_CHECKPOINT_MAP) continue;

                    uint count = U32(blk, 36);
                    for (uint j = 0; j < count && j < 4096; j++)
                    {
                        int eOff = 40 + (int)(j * 40);
                        if (eOff + 40 > blk.Length) break;
                        ulong oid   = U64(blk, eOff + 24);
                        ulong paddr = U64(blk, eOff + 32);
                        if (oid != 0)
                            ephemMap[oid] = paddr; // 나중 값으로 덮어쓰기 = 최신 우선
                    }
                }
            }

            // ── 방법 2(폴백): 체크포인트 데이터 영역을 직접 스캔해 APSB 찾기 ──────
            // 체크포인트 맵 파싱이 실패했거나 nx_fs_oid[]가 비어있을 때 사용
            var apsbBlocks = new List<ulong>(); // 데이터 영역에서 찾은 APSB 물리 블록

            if (xpDataBlocks > 0)
            {
                uint scanLen = Math.Min(xpDataLen > 0 ? xpDataLen : xpDataBlocks, 4096u);
                for (uint i = 0; i < scanLen; i++)
                {
                    ulong blkNum = xpDataBase + (xpDataIndex + i) % xpDataBlocks;
                    byte[] blk;
                    try { blk = ReadBlock(blkNum); } catch { continue; }
                    if (blk.Length > 36 && U32(blk, 32) == APSB)
                        apsbBlocks.Add(blkNum);
                }
            }

            // Walk nx_fs_oid[] to enumerate volumes (방법 1) — activeSb 사용
            uint maxFs = U32(activeSb, 180);
            if (maxFs > 100) maxFs = 100;

            for (uint i = 0; i < maxFs; i++)
            {
                ulong fsOid = U64(activeSb, 184 + (int)(i * 8));
                if (fsOid == 0) continue;
                if (!ephemMap.TryGetValue(fsOid, out ulong volPhys)) continue;

                AddVolumeFromBlock(volPhys, result);
            }

            // ── 방법 1b: ephemMap의 모든 paddr을 직접 APSB 후보로 시도 ─────────────
            if (result.Count == 0)
            {
                foreach (var paddr in ephemMap.Values)
                    AddVolumeFromBlock(paddr, result);
            }

            // ── 방법 2 폴백: 체크포인트 데이터 영역 직접 스캔 ──────────────────────
            if (result.Count == 0)
            {
                foreach (var blkNum in apsbBlocks)
                    AddVolumeFromBlock(blkNum, result);
            }

            // ── 방법 3: 컨테이너 OMAP으로 fsOid 조회 ────────────────────────────────
            if (result.Count == 0)
            {
                ulong containerOmapPhys = U64(activeSb, 160); // nx_omap_oid
                uint maxFs3 = U32(activeSb, 180); if (maxFs3 > 100) maxFs3 = 100;
                for (uint i = 0; i < maxFs3; i++)
                {
                    ulong fsOid3 = U64(activeSb, 184 + (int)(i * 8));
                    if (fsOid3 == 0) continue;
                    if (OmapLookup(containerOmapPhys, fsOid3, out ulong physAddr))
                        AddVolumeFromBlock(physAddr, result);
                }
            }

            // ── 방법 4: OID 값을 물리 블록 주소로 직접 시도 ─────────────────────────
            if (result.Count == 0)
            {
                uint maxFs4 = U32(activeSb, 180); if (maxFs4 > 100) maxFs4 = 100;
                for (uint i = 0; i < maxFs4; i++)
                {
                    ulong fsOid4 = U64(activeSb, 184 + (int)(i * 8));
                    if (fsOid4 != 0) AddVolumeFromBlock(fsOid4, result);
                }
            }

            // ── 방법 5: 데이터 영역 전체 스캔 — 가장 최근 위치부터 역방향 ────────────
            // 볼륨 수퍼블록이 이전 체크포인트에 기록된 경우 데이터 링 어딘가에 존재.
            // 현재 체크포인트 직전 위치부터 거꾸로 스캔해 최신 APSB를 먼저 찾음.
            if (result.Count == 0 && xpDataBlocks > 0)
            {
                uint totalScan = Math.Min(xpDataBlocks, 200000u);
                for (uint i = 0; i < totalScan; i++)
                {
                    // 현재 체크포인트 바로 앞 블록부터 역방향
                    ulong blkNum = xpDataBase + (xpDataIndex + xpDataBlocks - 1 - i) % xpDataBlocks;
                    byte[] b;
                    try { b = ReadBlock(blkNum); } catch { continue; }
                    if (b.Length <= 36) continue;
                    bool isApsbMagic = U32(b, 32) == APSB;
                    bool isFsType    = (U32(b, 24) & 0xFFFF) == 0x000D;
                    if (isApsbMagic || isFsType)
                    {
                        AddVolumeFromBlock(blkNum, result);
                        if (result.Count >= 10) break;
                    }
                }
            }

            // ── 방법 6: 32-byte checkpoint_mapping_t 구조로 재시도 ─────────────────
            // cpm_fs_oid 없는 버전: eOff += 32, oid at +16, paddr at +24
            if (result.Count == 0)
            {
                var ephemMap32 = new Dictionary<ulong, ulong>();
                if (xpDescBlocks > 0)
                {
                    uint scanB = Math.Min(xpDescBlocks, 4096u);
                    for (uint i = 0; i < scanB; i++)
                    {
                        byte[] blk;
                        try { blk = ReadBlock(xpDescBase + i); } catch { continue; }
                        if ((U32(blk, 24) & 0xFFFF) != TYPE_CHECKPOINT_MAP) continue;
                        uint count = U32(blk, 36);
                        for (uint j = 0; j < count && j < 4096; j++)
                        {
                            int eOff = 40 + (int)(j * 32);
                            if (eOff + 32 > blk.Length) break;
                            ulong oid   = U64(blk, eOff + 16);
                            ulong paddr = U64(blk, eOff + 24);
                            if (oid != 0) ephemMap32[oid] = paddr;
                        }
                    }
                }
                uint mf6 = U32(sb, 180); if (mf6 > 100) mf6 = 100;
                for (uint i = 0; i < mf6; i++)
                {
                    ulong fsOid6 = U64(sb, 184 + (int)(i * 8));
                    if (fsOid6 == 0) continue;
                    if (ephemMap32.TryGetValue(fsOid6, out ulong ph6))
                        AddVolumeFromBlock(ph6, result);
                }
            }

            // 모든 방법 실패 시 디버그 정보 포함 예외
            if (result.Count == 0)
            {
                ulong totalBlocks = U64(sb, 40);
                uint maxFs2 = U32(sb, 180); if (maxFs2 > 10) maxFs2 = 10;
                var fsOids = string.Join(",", Enumerable.Range(0, (int)maxFs2)
                    .Select(i => U64(sb, 184 + i * 8)).Where(v => v != 0));
                // activeSb(최신 체크포인트 수퍼블록)의 fsOids
                uint maxFsA = U32(activeSb, 180); if (maxFsA > 10) maxFsA = 10;
                var fsOidsActive = string.Join(",", Enumerable.Range(0, (int)maxFsA)
                    .Select(i => U64(activeSb, 184 + i * 8)).Where(v => v != 0));
                ulong activeXid = U64(activeSb, 16); // o_xid at offset 16
                var ephemPairs = string.Join("|", ephemMap.Take(8)
                    .Select(kv => $"{kv.Key}>{kv.Value}"));

                // ── 체크포인트 맵 블록 raw 스캔: OID 1026 바이트 패턴 직접 탐색 ──────
                // 어떤 오프셋이든 OID 값이 있으면 그 위치와 주변 8바이트를 보고함
                var oid1026bytes = BitConverter.GetBytes((ulong)1026); // little-endian
                string rawScanDbg = "notfound";
                // 체크포인트 맵 블록들 타입 목록
                var cmBlockTypes = new System.Text.StringBuilder();
                try
                {
                    uint scanBlocks = Math.Min(xpDescBlocks, 4096u);
                    for (uint i = 0; i < scanBlocks && rawScanDbg == "notfound"; i++)
                    {
                        ulong blkNum = xpDescBase + i;
                        byte[] blk;
                        try { blk = ReadBlock(blkNum); } catch { continue; }

                        ushort objType = (ushort)(U32(blk, 24) & 0xFFFF);
                        if (i < 6) cmBlockTypes.Append($"[{blkNum}:{objType:X}]");

                        // 이 블록에서 OID 1026 값을 raw 탐색
                        for (int pos = 0; pos <= blk.Length - 8; pos++)
                        {
                            bool match = true;
                            for (int b2 = 0; b2 < 8; b2++)
                                if (blk[pos + b2] != oid1026bytes[b2]) { match = false; break; }
                            if (match)
                            {
                                // 앞뒤 8바이트 포함해 32바이트 덤프
                                int dumpStart = Math.Max(0, pos - 8);
                                int dumpLen   = Math.Min(32, blk.Length - dumpStart);
                                string dump = BitConverter.ToString(blk, dumpStart, dumpLen).Replace("-", "");
                                rawScanDbg = $"blk={blkNum} pos={pos} type={objType:X} dump={dump}";
                                break;
                            }
                        }
                    }
                }
                catch (Exception ex) { rawScanDbg = "ERR:" + ex.Message; }

                // ── 체크포인트 맵 첫 항목 raw bytes (블록 idx, idx+1 모두 시도) ──────
                string mapRaw0 = "?", mapRaw1 = "?";
                try
                {
                    ulong cmBlk0 = xpDescBase + xpDescIndex % xpDescBlocks;
                    var d = ReadBlock(cmBlk0);
                    ushort t = (ushort)(U32(d, 24) & 0xFFFF);
                    int dumpLen = Math.Min(80, d.Length - 40);
                    if (dumpLen > 0)
                        mapRaw0 = $"t={t:X} [{BitConverter.ToString(d, 40, dumpLen).Replace("-","")}]";
                }
                catch { mapRaw0 = "ERR"; }
                try
                {
                    ulong cmBlk1 = xpDescBase + (xpDescIndex + 1) % xpDescBlocks;
                    var d = ReadBlock(cmBlk1);
                    ushort t = (ushort)(U32(d, 24) & 0xFFFF);
                    int dumpLen = Math.Min(80, d.Length - 40);
                    if (dumpLen > 0)
                        mapRaw1 = $"t={t:X} [{BitConverter.ToString(d, 40, dumpLen).Replace("-","")}]";
                }
                catch { mapRaw1 = "ERR"; }

                // ── OMAP 블록 dump ────────────────────────────────────────────────────
                ulong omapPhysDbg = U64(activeSb, 160);
                string omapBlkDbg = "?", omapTreeDbg = "?";
                string omapDbg2 = "?";
                try
                {
                    ulong omapResolved = omapPhysDbg;
                    if (ephemMap.TryGetValue(omapPhysDbg, out ulong ep)) omapResolved = ep;
                    var omBlk = ReadBlock(omapResolved);
                    ushort omType = (ushort)(U32(omBlk, 24) & 0xFFFF);
                    int omDumpLen = Math.Min(64, omBlk.Length);
                    omapBlkDbg = $"phys={omapResolved} t={omType:X} [{BitConverter.ToString(omBlk, 0, omDumpLen).Replace("-","")}]";

                    if (omType == 0x000B) // TYPE_OMAP
                    {
                        ulong treePhys = U64(omBlk, 48);
                        var tBlk = ReadBlock(treePhys);
                        ushort tType = (ushort)(U32(tBlk, 24) & 0xFFFF);
                        uint nkeys = U32(tBlk, 36);
                        int tDumpLen = Math.Min(64, tBlk.Length);
                        omapTreeDbg = $"treePhys={treePhys} t={tType:X} nkeys={nkeys} [{BitConverter.ToString(tBlk, 0, tDumpLen).Replace("-","")}]";
                    }

                    bool found = OmapLookup(omapResolved, 1026, out ulong r1026);
                    omapDbg2 = $"found={found} res1026={r1026}";
                }
                catch (Exception ex) { omapDbg2 = "ERR:" + ex.Message; }

                // ── 데이터 영역 바깥 스캔 (27954~30000) ──────────────────────────────
                string extraScan = "0";
                try
                {
                    ulong extraStart = xpDataBase + xpDataBlocks;
                    ulong extraEnd   = Math.Min(extraStart + 3000, totalBlocks);
                    int   extraFound = 0;
                    for (ulong bn = extraStart; bn < extraEnd; bn++)
                    {
                        byte[] b; try { b = ReadBlock(bn); } catch { continue; }
                        if (b.Length > 36 && U32(b, 32) == APSB) { extraFound++; AddVolumeFromBlock(bn, result); }
                    }
                    extraScan = extraFound.ToString();
                }
                catch { extraScan = "ERR"; }

                throw new Exception(
                    $"APFS 볼륨 없음\n" +
                    $"sb[32]={U32(sb,32):X} bsz={_blockSize}\n" +
                    $"descBase={xpDescBase} idx={xpDescIndex} len={xpDescLen} tot={xpDescBlocks}\n" +
                    $"dataBase={xpDataBase} idx={xpDataIndex} len={xpDataLen} tot={xpDataBlocks}\n" +
                    $"blocks={totalBlocks} ephemMapSize={ephemMap.Count} apsbScan={apsbBlocks.Count}\n" +
                    $"fsOids_blk0=[{fsOids}] fsOids_active=[{fsOidsActive}] activeXid={activeXid}\n" +
                    $"active_dataIdx={U32(activeSb,144)} active_dataLen={U32(activeSb,148)}\n" +
                    $"ephemPairs={ephemPairs}\n" +
                    $"descBlkTypes={cmBlockTypes}\n" +
                    $"mapRaw_idx={mapRaw0}\n" +
                    $"mapRaw_idx1={mapRaw1}\n" +
                    $"oid1026scan={rawScanDbg}\n" +
                    $"omapBlk={omapBlkDbg}\n" +
                    $"omapTree={omapTreeDbg}\n" +
                    $"omapLookup={omapDbg2}\n" +
                    $"extraScan(27954+)={extraScan}");
            }

            return result;
        }

        private void AddVolumeFromBlock(ulong physBlock, List<ApfsVolumeInfo> result)
        {
            byte[] volBlk;
            try { volBlk = ReadBlock(physBlock); } catch { return; }
            if (U32(volBlk, 32) != APSB) return;

            // apfs_superblock_t offsets:
            //  56: apfs_incompatible_features (8)
            // 128: apfs_omap_oid (8)        — volume OMAP OID (physical)
            // 136: apfs_root_tree_oid (8)   — FS root B-tree virtual OID
            // 144: apfs_extentref_tree_oid  (not used here)
            // 480: apfs_volname (256 bytes UTF-8)

            ulong incompat    = U64(volBlk, 56);
            bool  encrypted   = (incompat & 0x80UL) != 0;
            ulong omapPhys    = U64(volBlk, 128);
            ulong rootTreeOid = U64(volBlk, 136);

            // apfs_fs_alloc_count (offset 88): 이 볼륨에 현재 할당된 블록 수
            ulong allocCount = U64(volBlk, 88);
            ulong usedMB     = allocCount * _blockSize / 1024 / 1024;

            string name = "";
            try
            {
                int nameEnd = Array.IndexOf(volBlk, (byte)0, 480, 256);
                int nameLen = (nameEnd < 0 ? 480 + 256 : nameEnd) - 480;
                name = Encoding.UTF8.GetString(volBlk, 480, Math.Max(0, nameLen));
            }
            catch { }

            if (encrypted) name += " (암호화됨, 목록 불가)";

            result.Add(new ApfsVolumeInfo
            {
                PhysBlock   = physBlock,
                OmapPhys    = encrypted ? 0 : omapPhys,
                RootTreeOid = encrypted ? 0 : rootTreeOid,
                Name        = name,
                UsedMB      = usedMB
            });
        }

        // ── OMAP B-tree: virtual OID → physical block ────────────────────────

        public bool OmapLookup(ulong omapPhysBlock, ulong targetOid, out ulong physAddr)
        {
            physAddr = 0;
            try
            {
                var omapData = ReadBlock(omapPhysBlock);
                ushort objType = (ushort)(U32(omapData, 24) & 0xFFFF);

                ulong treePhys;
                if (objType == TYPE_OMAP)
                    treePhys = U64(omapData, 48); // om_tree_oid (physical for OMAP nodes)
                else if (objType == TYPE_BTREE_NODE)
                    treePhys = omapPhysBlock;
                else
                    return false;

                return SearchOmapTree(treePhys, targetOid, out physAddr);
            }
            catch { return false; }
        }

        private bool SearchOmapTree(ulong nodePhys, ulong targetOid, out ulong physAddr)
        {
            physAddr = 0;
            var visited = new HashSet<ulong>();
            ulong cur = nodePhys;

            while (visited.Add(cur) && visited.Count < 10000)
            {
                byte[] nd;
                try { nd = ReadBlock(cur); } catch { return false; }

                ushort flags    = U16(nd, 32);
                uint   nkeys    = U32(nd, 36);
                ushort tocOff   = U16(nd, 40); // btn_table_space.off
                ushort tableLen = U16(nd, 42); // btn_table_space.len

                bool isLeaf = (flags & BTNODE_LEAF) != 0;
                bool isRoot = (flags & BTNODE_ROOT) != 0;

                const int DATA_BASE = 56;
                int tocStart    = DATA_BASE + tocOff;
                // 키 영역은 TOC 영역 다음에 시작 (FIXED_KV_SIZE 노드는 tableLen이 큼)
                int keyAreaBase = DATA_BASE + tocOff + tableLen;
                int trailing    = isRoot ? 40 : 0;

                ulong bestKey = 0;
                ulong bestChild = 0;
                bool  bestFound = false;

                for (int i = 0; i < (int)nkeys; i++)
                {
                    int eOff = tocStart + i * 4; // kvoff_t: 2+2
                    if (eOff + 4 > nd.Length) break;

                    ushort keyOff = U16(nd, eOff);
                    ushort valOff = U16(nd, eOff + 2);
                    if (valOff == 0xFFFF) continue; // deleted/invalid entry

                    int kPos = keyAreaBase + keyOff;
                    if (kPos + 8 > nd.Length) continue;

                    ulong keyOid = U64(nd, kPos); // ok_oid (first 8 bytes of omap_key_t)

                    int vPos = (int)_blockSize - trailing - valOff;
                    if (vPos < 0 || vPos + 8 > nd.Length) continue;

                    if (isLeaf)
                    {
                        if (keyOid == targetOid)
                        {
                            // omap_val_t: flags(4) pad(4) paddr(8)
                            physAddr = U64(nd, vPos + 8);
                            return true;
                        }
                    }
                    else
                    {
                        if (keyOid <= targetOid && (!bestFound || keyOid >= bestKey))
                        {
                            bestKey   = keyOid;
                            bestChild = U64(nd, vPos); // child node physical address
                            bestFound = true;
                        }
                    }
                }

                if (!isLeaf && bestFound)
                    cur = bestChild;
                else
                    break;
            }
            return false;
        }

        // ── FS B-tree: collect all DREC + INODE records ──────────────────────

        public List<object> WalkVolume(ulong omapPhys, ulong rootTreeVirtualOid)
        {
            if (!OmapLookup(omapPhys, rootTreeVirtualOid, out ulong rootPhys))
                throw new Exception("APFS FS 트리 루트를 찾을 수 없습니다.");

            var drecs      = new Dictionary<ulong, List<(string name, ulong childOid, bool isDir)>>();
            var inodes     = new Dictionary<ulong, long>();
            var inodeDirs  = new Dictionary<ulong, bool>(); // inode OID → isDir (from mode field)
            var inodeMtimes = new Dictionary<ulong, string>(); // inode OID → mod_time (yyyy-MM-dd)
            var visited    = new HashSet<ulong>();

            CollectRecords(rootPhys, omapPhys, drecs, inodes, inodeDirs, inodeMtimes, visited);

            var tree = BuildTree(ROOT_INO, drecs, inodes, inodeDirs, inodeMtimes, new HashSet<ulong>());

            // 결과가 비어있거나 파일이 없으면 디버그 정보 예외
            int fileCount = 0;
            void CountAll(List<object> nodes) {
                foreach (var n in nodes) {
                    var t = n.GetType();
                    var typeProp = t.GetProperty("type");
                    if (typeProp?.GetValue(n)?.ToString() == "file") fileCount++;
                    else {
                        var ch = t.GetProperty("children")?.GetValue(n) as List<object>;
                        if (ch != null) CountAll(ch);
                    }
                }
            }
            CountAll(tree);

            if (fileCount == 0)
            {
                // 루트 노드 첫 항목 raw bytes 덤프
                string rootDump = "?";
                try {
                    var rb = ReadBlock(rootPhys);
                    ushort rf = U16(rb,32); uint rn = U32(rb,36);
                    ushort rt = U16(rb,40); ushort rl = U16(rb,42);
                    int keyBase = 56 + rt + rl;
                    string firstKey = rb.Length > keyBase + 16
                        ? BitConverter.ToString(rb, keyBase, 16).Replace("-","") : "?";
                    rootDump = $"flags={rf:X} nkeys={rn} tocOff={rt} tableLen={rl} keyBase={keyBase} firstKey={firstKey}";
                } catch { rootDump = "ERR"; }

                // drecs[2] 내용
                string drecs2 = drecs.TryGetValue(2, out var r2)
                    ? $"{r2.Count}개:[{string.Join(",", r2.Take(3).Select(x=>x.name))}]" : "없음";
                string topOids = string.Join(",", drecs.Keys.Take(8));

                throw new Exception(
                    $"파일 없음 (디버그)\n" +
                    $"nodesVisited={visited.Count} drecs.Keys={drecs.Count} inodes={inodes.Count}\n" +
                    $"rootPhys={rootPhys} omapPhys={omapPhys}\n" +
                    $"rootNode={rootDump}\n" +
                    $"drecs[2]={drecs2}\n" +
                    $"drecOIDs=[{topOids}]");
            }

            return tree;
        }

        // ── 스트리밍 순회 (트리를 메모리에 만들지 않음, OOM 방지) ──────────────────
        // BuildTree와 동일한 분류 규칙을 쓰되, 노드를 만들 때마다 emit으로 흘려보낸다.
        public int WalkVolumeStream(ulong omapPhys, ulong rootTreeVirtualOid,
                                    Action<string, string, bool, long, string> emit)
        {
            if (!OmapLookup(omapPhys, rootTreeVirtualOid, out ulong rootPhys))
                throw new Exception("APFS FS 트리 루트를 찾을 수 없습니다.");

            var drecs       = new Dictionary<ulong, List<(string name, ulong childOid, bool isDir)>>();
            var inodes      = new Dictionary<ulong, long>();
            var inodeDirs   = new Dictionary<ulong, bool>();
            var inodeMtimes = new Dictionary<ulong, string>();
            var visited     = new HashSet<ulong>();

            CollectRecords(rootPhys, omapPhys, drecs, inodes, inodeDirs, inodeMtimes, visited);

            int count = 0;
            Action<string, string, bool, long, string> w = (pp, n, d, s, m) => { count++; emit(pp, n, d, s, m); };
            EmitTree(ROOT_INO, "", drecs, inodes, inodeDirs, inodeMtimes, new HashSet<ulong>(), w);

            if (count == 0)
                throw new Exception(
                    $"APFS 볼륨에서 파일을 찾지 못했습니다.\n" +
                    $"drecs.Keys={drecs.Count} inodes={inodes.Count} nodesVisited={visited.Count} rootPhys={rootPhys}");
            return count;
        }

        // BuildTree의 스트리밍 버전: 트리를 반환하지 않고 emit으로 전달
        private static void EmitTree(
            ulong dirOid, string parentPath,
            Dictionary<ulong, List<(string name, ulong childOid, bool isDir)>> drecs,
            Dictionary<ulong, long> inodes,
            Dictionary<ulong, bool> inodeDirs,
            Dictionary<ulong, string> inodeMtimes,
            HashSet<ulong> seen,
            Action<string, string, bool, long, string> emit)
        {
            if (!drecs.TryGetValue(dirOid, out var entries)) return;
            if (!seen.Add(dirOid)) return; // cycle guard

            foreach (var (name, childOid, isDir) in entries)
            {
                string mtime = inodeMtimes.TryGetValue(childOid, out var mt) ? mt : "";
                bool hasExtension = name.Contains('.') &&
                                    name.LastIndexOf('.') < name.Length - 1 &&
                                    name.LastIndexOf('.') > 0;
                string full = parentPath.Length == 0 ? name : parentPath + "/" + name;

                if (hasExtension)
                {
                    long sizeKB = inodes.TryGetValue(childOid, out long s) ? s : 1;
                    emit(parentPath, name, false, sizeKB, mtime);
                    continue;
                }

                bool actualIsDir = inodeDirs.TryGetValue(childOid, out bool modeDir) ? modeDir : isDir;
                if (actualIsDir)
                {
                    emit(parentPath, name, true, 0, "");
                    EmitTree(childOid, full, drecs, inodes, inodeDirs, inodeMtimes, seen, emit);
                }
                else
                {
                    long sizeKB = inodes.TryGetValue(childOid, out long s) ? s : 1;
                    emit(parentPath, name, false, sizeKB, mtime);
                }
            }
        }

        private void CollectRecords(
            ulong nodePhys,
            ulong omapPhys,
            Dictionary<ulong, List<(string, ulong, bool)>> drecs,
            Dictionary<ulong, long> inodes,
            Dictionary<ulong, bool> inodeDirs,
            Dictionary<ulong, string> inodeMtimes,
            HashSet<ulong> visited)
        {
            var stack = new Stack<ulong>();
            stack.Push(nodePhys);

            while (stack.Count > 0)
            {
                ulong cur = stack.Pop();
                // ① 순환 방지(cycle guard): 이미 방문한 블록은 정상적으로 건너뛴다.
                if (!visited.Add(cur)) continue;
                // ② 폭주 방어선: 정상 4TB 드라이브의 FS B-tree 노드 수를 충분히 포괄하도록 상향(2천만).
                //    이 한계에 도달했다는 것은 정상 구조가 아니라 디스크 손상/순환 포인터일 가능성이 높다.
                //    조용히 누락(silent data loss)하는 대신 명시적 예외로 스캔을 실패시켜 사용자에게 알린다.
                if (visited.Count > 20_000_000)
                    throw new Exception(
                        "APFS 스캔 노드 수가 한계(20,000,000)를 초과했습니다.\n" +
                        "디스크 손상 또는 비정상적인 파일시스템 구조일 수 있습니다.\n" +
                        "데이터 무결성을 위해 불완전한 결과를 저장하지 않고 스캔을 중단합니다.");

                byte[] nd;
                try { nd = ReadBlock(cur); } catch { continue; }

                ushort flags  = U16(nd, 32);
                uint   nkeys    = U32(nd, 36);
                ushort tocOff   = U16(nd, 40);
                ushort tableLen = U16(nd, 42); // btn_table_space.len

                bool isLeaf = (flags & BTNODE_LEAF) != 0;
                bool isRoot = (flags & BTNODE_ROOT) != 0;

                const int DATA_BASE = 56;
                int tocStart    = DATA_BASE + tocOff;
                int keyAreaBase = DATA_BASE + tocOff + tableLen;
                int trailing    = isRoot ? 40 : 0;

                // ── 블록별 TOC 형식 자동 감지 (4-byte kvoff_t vs 8-byte kvloc_t) ──────
                int tocStride = DetectTocStride(nd, tocStart, keyAreaBase, nkeys, trailing);

                for (int i = 0; i < (int)nkeys; i++)
                {
                    int eOff = tocStart + i * tocStride;
                    if (eOff + tocStride > nd.Length) break;

                    ushort keyOff = U16(nd, eOff);
                    // 4-byte: valOff = bytes[2-3], 8-byte kvloc_t: valOff = bytes[4-5] (v_off)
                    ushort valOff = (tocStride == 8) ? U16(nd, eOff + 4) : U16(nd, eOff + 2);
                    if (valOff == 0xFFFF) continue;

                    int kPos = keyAreaBase + keyOff;
                    if (kPos + 8 > nd.Length) continue;

                    int vPos = (int)_blockSize - trailing - valOff;

                    if (!isLeaf)
                    {
                        // Internal node value = virtual OID of child FS node
                        if (vPos >= 0 && vPos + 8 <= nd.Length)
                        {
                            ulong childVOid = U64(nd, vPos);
                            if (OmapLookup(omapPhys, childVOid, out ulong childPhys))
                                stack.Push(childPhys);
                            else if (childVOid > 0 && childVOid < 0x8000000000000000UL)
                                stack.Push(childVOid); // OMAP 실패 시 virtual OID를 물리 주소로 직접 시도
                        }
                        continue;
                    }

                    // Leaf: parse by record type
                    ulong objIdAndType = U64(nd, kPos);
                    ulong recType      = objIdAndType >> 60;
                    ulong oid          = objIdAndType & 0x0FFFFFFFFFFFFFFFUL;

                    if (recType == REC_DIR_REC)
                    {
                        // j_drec_hashed_key_t:
                        //  kPos+0:  obj_id_and_type (8) — oid = parent directory inode
                        //  kPos+8:  name_len_and_hash (4) — low 10 bits = len (incl. null)
                        //  kPos+12: name (UTF-8, null-terminated)
                        if (kPos + 12 > nd.Length || vPos < 0 || vPos + 10 > nd.Length) continue;

                        uint nlh     = U32(nd, kPos + 8);
                        int  nameLen = (int)(nlh & 0x3FF);
                        if (nameLen <= 1 || kPos + 12 + nameLen > nd.Length) continue;

                        string name;
                        try { name = Encoding.UTF8.GetString(nd, kPos + 12, nameLen - 1); }
                        catch { continue; }

                        if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;

                        ulong  fileId = U64(nd, vPos);
                        // j_drec_val_t.flags: offset 16 when date_added(8) present, else offset 8
                        ushort dflags = (vPos + 18 <= nd.Length) ? U16(nd, vPos + 16) :
                                       (vPos + 10 <= nd.Length) ? U16(nd, vPos +  8) : (ushort)0;
                        int    dtype  = dflags & 0xF;
                        // dtype 4=dir, 8=regular; if unknown(0), resolve in BuildTree via drecs
                        bool   isDir  = dtype == 4;

                        if (!drecs.TryGetValue(oid, out var list))
                        {
                            list = new List<(string, ulong, bool)>();
                            drecs[oid] = list;
                        }
                        list.Add((name, fileId, isDir));
                    }
                    else if (recType == REC_INODE)
                    {
                        // j_inode_val_t:
                        //  vPos+0:  parent_id (8)
                        //  vPos+8:  private_id (8)
                        //  vPos+16: create_time (8)
                        //  vPos+24: mod_time (8)
                        //  vPos+32: change_time (8)
                        //  vPos+40: access_time (8)
                        //  vPos+48: internal_flags (8)
                        //  vPos+56: nchildren/nlink (4)
                        //  vPos+60: protection_class (4)
                        //  vPos+64: write_generation_counter (4)
                        //  vPos+68: bsd_flags (4)
                        //  vPos+72: owner (4)
                        //  vPos+76: group (4)
                        //  vPos+80: mode (2)
                        //  vPos+82: pad1 (2)
                        //  vPos+84: pad2/reserved — 파일 크기 아님 (xfields에서 읽어야 함)
                        if (vPos + 82 <= nd.Length)
                        {
                            ushort mode = U16(nd, vPos + 80);
                            bool isDir4000 = (mode & 0xF000) == 0x4000;
                            // 같은 oid가 이미 있으면 파일(false) 우선 — 가비지 블록이 file을 dir로 덮어쓰지 못하게
                            if (!inodeDirs.ContainsKey(oid) || !isDir4000)
                                inodeDirs[oid] = isDir4000;

                            // mod_time (vPos+24): APFS nanoseconds since 1970 UTC
                            if (vPos + 32 <= nd.Length)
                            {
                                ulong ns = U64(nd, vPos + 24);
                                if (ns > 0)
                                {
                                    try {
                                        var dt = DateTimeOffset.FromUnixTimeSeconds((long)(ns / 1_000_000_000UL));
                                        inodeMtimes[oid] = dt.ToString("yyyy-MM-dd");
                                    } catch { }
                                }
                            }

                            // 파일 크기 결정:
                            //  1순위: j_inode_val_t.uncompressed_size (vPos+84) — 비압축 파일의 실제 크기
                            //  2순위(보정): xfields의 DSTREAM(type=0x10) j_dstream.size
                            long sizeKB = 1;
                            const long MAX_SZ = 100L * 1024 * 1024 * 1024 * 1024; // 100TB 상한(쓰레기값 방어)
                            if (vPos + 92 <= nd.Length)
                            {
                                long us = (long)U64(nd, vPos + 84);
                                if (us > 0 && us < MAX_SZ) sizeKB = Math.Max(1, us / 1024);

                                int xBase = vPos + 92;
                                if (xBase + 4 <= nd.Length)
                                {
                                    int xfNum = U16(nd, xBase);
                                    int xfUsed = U16(nd, xBase + 2);
                                    int xPos = xBase + 4;
                                    for (int x = 0; x < xfNum && xPos + 4 <= nd.Length; x++)
                                    {
                                        byte xType  = nd[xPos];
                                        byte xFlags = nd[xPos + 1];
                                        ushort xLen = U16(nd, xPos + 2);
                                        xPos += 4;
                                        if (xType == 0x10 && xPos + 8 <= nd.Length) // DSTREAM
                                        {
                                            long fsize = (long)U64(nd, xPos);
                                            if (fsize > 0 && fsize < MAX_SZ)
                                                sizeKB = Math.Max(1, fsize / 1024);
                                            break;
                                        }
                                        xPos += xLen;
                                        // 4-byte 정렬
                                        if (xLen % 4 != 0) xPos += 4 - (xLen % 4);
                                    }
                                }
                            }
                            inodes[oid] = sizeKB;
                        }
                        else
                        {
                            inodes[oid] = 1;
                        }
                    }
                }
            }
        }

        private static List<object> BuildTree(
            ulong dirOid,
            Dictionary<ulong, List<(string name, ulong childOid, bool isDir)>> drecs,
            Dictionary<ulong, long> inodes,
            Dictionary<ulong, bool> inodeDirs,
            Dictionary<ulong, string> inodeMtimes,
            HashSet<ulong> seen)
        {
            var result = new List<object>();
            if (!drecs.TryGetValue(dirOid, out var entries)) return result;
            if (!seen.Add(dirOid)) return result; // cycle guard

            foreach (var (name, childOid, isDir) in entries)
            {
                string mtime = inodeMtimes.TryGetValue(childOid, out var mt) ? mt : "";
                // 확장자 있는 항목(name.ext)은 Mac 패키지 포함 모두 파일로 처리
                // 일반 폴더는 확장자가 없음 (예: "테스트 촬영", "인생4컷")
                bool hasExtension = name.Contains('.') &&
                                    name.LastIndexOf('.') < name.Length - 1 &&
                                    name.LastIndexOf('.') > 0;
                if (hasExtension)
                {
                    long sizeKB = inodes.TryGetValue(childOid, out long s) ? s : 1;
                    result.Add(new { type = "file", name, size = sizeKB, mtime });
                    continue;
                }

                // 확장자 없는 항목: inode mode → drec dtype 순으로 폴더 여부 판별
                bool actualIsDir;
                if (inodeDirs.TryGetValue(childOid, out bool modeDir))
                    actualIsDir = modeDir;
                else
                    actualIsDir = isDir;

                if (actualIsDir)
                {
                    var children = BuildTree(childOid, drecs, inodes, inodeDirs, inodeMtimes, seen);
                    result.Add(new { type = "dir", name, children });
                }
                else
                {
                    long sizeKB = inodes.TryGetValue(childOid, out long s) ? s : 1;
                    result.Add(new { type = "file", name, size = sizeKB, mtime });
                }
            }

            return result;
        }

        // ── 블록의 TOC 항목 크기 자동 감지 ────────────────────────────────────────
        // 4-byte kvoff_t vs 8-byte kvloc_t 중 더 많은 유효 레코드를 주는 쪽을 선택
        private int DetectTocStride(byte[] nd, int tocStart, int keyAreaBase, uint nkeys, int trailing)
        {
            int score4 = 0, score8 = 0;
            int check = (int)Math.Min(nkeys, 8u);

            for (int i = 0; i < check; i++)
            {
                // ── 4-byte stride 테스트 ──────────────────────────────────────────
                int e4 = tocStart + i * 4;
                if (e4 + 4 <= nd.Length)
                {
                    ushort ko4 = U16(nd, e4);
                    ushort vo4 = U16(nd, e4 + 2);
                    if (vo4 != 0xFFFF)
                    {
                        int kp4 = keyAreaBase + ko4;
                        int vp4 = (int)_blockSize - trailing - vo4;
                        if (kp4 + 8 <= nd.Length && vp4 >= 0 && vp4 + 10 <= nd.Length)
                        {
                            ulong oat4 = U64(nd, kp4);
                            ulong rt4  = oat4 >> 60;
                            if (rt4 >= 2 && rt4 <= 13)
                            {
                                // DREC: fileId 검증
                                if (rt4 == 9 && vp4 + 18 <= nd.Length)
                                {
                                    ulong fid4 = U64(nd, vp4);
                                    if (fid4 > 0 && fid4 < (1UL << 40)) score4 += 2;
                                    else score4++;
                                }
                                else score4++;
                            }
                        }
                    }
                }

                // ── 8-byte stride 테스트 ──────────────────────────────────────────
                int e8 = tocStart + i * 8;
                if (e8 + 8 <= nd.Length)
                {
                    ushort ko8 = U16(nd, e8);
                    ushort vo8 = U16(nd, e8 + 4); // v_off (bytes 4-5)
                    if (vo8 != 0xFFFF)
                    {
                        int kp8 = keyAreaBase + ko8;
                        int vp8 = (int)_blockSize - trailing - vo8;
                        if (kp8 + 8 <= nd.Length && vp8 >= 0 && vp8 + 10 <= nd.Length)
                        {
                            ulong oat8 = U64(nd, kp8);
                            ulong rt8  = oat8 >> 60;
                            if (rt8 >= 2 && rt8 <= 13)
                            {
                                if (rt8 == 9 && vp8 + 18 <= nd.Length)
                                {
                                    ulong fid8 = U64(nd, vp8);
                                    if (fid8 > 0 && fid8 < (1UL << 40)) score8 += 2;
                                    else score8++;
                                }
                                else score8++;
                            }
                        }
                    }
                }
            }

            return (score8 > score4) ? 8 : 4;
        }

        public void Dispose() { /* stream owned by caller */ }
    }
}
