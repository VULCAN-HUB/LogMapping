// scanner.js — Worker Thread로 실행되는 스캐너
const { workerData, parentPort } = require('worker_threads');
const fs   = require('fs');
const path = require('path');

const folderPath = workerData.folderPath;
let fileCount = 0;
let lastReport = 0;

function walkDir(dirPath) {
  const result = [];
  let entries;
  try { entries = fs.readdirSync(dirPath); } catch(e) { return result; }

  for (const entry of entries) {
    if (entry.startsWith('.')) continue;
    const fullPath = path.join(dirPath, entry);
    let stat;
    try { stat = fs.statSync(fullPath); } catch(e) { continue; }

    if (stat.isDirectory()) {
      result.push({ type:'dir', name:entry, children:walkDir(fullPath) });
    } else {
      fileCount++;
      if (fileCount - lastReport >= 1000) {
        parentPort.postMessage({ type:'progress', count:fileCount });
        lastReport = fileCount;
      }
      result.push({ type:'file', name:entry, size:Math.round(stat.size/1024)||1 });
    }
  }
  return result;
}

try {
  const tree = walkDir(folderPath);
  parentPort.postMessage({ type:'done', tree, totalFiles:fileCount });
} catch(e) {
  parentPort.postMessage({ type:'error', message:e.message });
}
