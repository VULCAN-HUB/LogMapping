'use strict';
let updateState = null, updatePending = false, showingLicenses = false;
function aboutError(error) { document.getElementById('about-error').textContent = error ? error.message || String(error) : ''; }
async function openAbout() {
  document.getElementById('about-overlay').style.display = 'flex';
  aboutError(null);
  try {
    const info = await callNative('appInfo');
    document.getElementById('about-version').textContent = info.version + (info.channel === 'beta' ? ' · 베타 / PREVIEW' : '');
    document.getElementById('about-platform').textContent = info.platform;
    const result = await callNative('updateStatus');
    renderUpdate(result.status);
  } catch (e) { aboutError(e); }
}
function closeAbout() { document.getElementById('about-overlay').style.display = 'none'; }
async function openBrand(key) {
  try { await callNative('openUrl', { content: key }); } catch (e) { aboutError(e); }
}
async function showFontLicenses() {
  showingLicenses = !showingLicenses;
  document.getElementById('about-details').hidden = showingLicenses;
  document.getElementById('font-licenses').hidden = !showingLicenses;
  document.getElementById('font-license-button').textContent = showingLicenses ? '← 프로그램 정보' : '글꼴 라이선스';
  if (!showingLicenses) return;
  try {
    const texts = await Promise.all(['Rajdhani-OFL.txt', 'Pretendard-OFL.txt'].map(async name => {
      const response = await fetch('fonts/' + name);
      if (!response.ok) throw Error('글꼴 라이선스를 불러올 수 없습니다.');
      return name + '\n\n' + await response.text();
    }));
    document.getElementById('font-license-text').textContent = texts.join('\n\n────────────────\n\n');
  } catch (e) { document.getElementById('font-license-text').textContent = e.message; }
}
function renderUpdate(state) {
  if (!state) return;
  updateState = state;
  // Native serializer uses its default PascalCase for records.
  const value = key => state[key];
  const phase = value('State');
  if (phase === 'installing') _closingForExit = true;
  const busy = updatePending || phase === 'checking' || phase === 'downloading' || phase === 'installing';
  document.getElementById('update-channel').textContent = '현재 ' + value('CurrentVersion') + ' · ' + (value('Channel') === 'beta' ? '베타 채널 (테스트판 포함)' : '정식 채널');
  document.getElementById('update-status').textContent = value('Message') + (phase === 'downloading' ? ' ' + (value('Received') / 1048576).toFixed(1) + ' / ' + (value('Total') / 1048576).toFixed(1) + ' MiB' : '');
  document.getElementById('update-check').disabled = busy;
  document.getElementById('update-download').hidden = phase !== 'available';
  document.getElementById('update-download').disabled = busy;
  document.getElementById('update-cancel').hidden = phase !== 'downloading';
  document.getElementById('update-open').hidden = phase !== 'ready' || !value('CanOpen');
  document.getElementById('update-open').disabled = busy;
  document.getElementById('update-apply').hidden = phase !== 'ready' || !value('CanOpen');
  document.getElementById('update-apply').disabled = busy;
  const progress = document.getElementById('update-progress');
  progress.hidden = phase !== 'downloading'; progress.max = value('Total') || 1; progress.value = value('Received') || 0;
}
async function updateAction(action) {
  if (updatePending && action !== 'updateCancel') return;
  const cancelling = action === 'updateCancel';
  if (!cancelling) updatePending = true;
  aboutError(null); renderUpdate(updateState);
  try { const result = await callNative(action, {}, 31 * 60 * 1000); renderUpdate(result.status); }
  catch (e) { aboutError(e); }
  finally { if (!cancelling) updatePending = false; renderUpdate(updateState); }
}
async function openUpdateFolder() {
  if (_closingForExit || _catalogLoading || _mutations || _jobs || _scanProgressCb) {
    aboutError(Error('진행 중인 스캔·저장·내보내기가 끝난 뒤 확인하세요.')); return;
  }
  if (!confirm('수동으로 교체하려면 작업을 저장하고 앱을 정상 종료한 뒤 기존 실행 위치의 EXE만 교체하세요. .hcat과 data 폴더는 그대로 보존하세요.\n\n검증된 파일이 있는 폴더를 열까요?')) return;
  await updateAction('updateOpenFolder');
}
if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', event => {
  if (event.data && event.data.type === 'updateState') renderUpdate(event.data.data);
});

async function applyUpdate() {
  if (_closingForExit || _catalogLoading || _mutations || _jobs || _scanProgressCb) {
    aboutError(Error('진행 중인 스캔·저장·내보내기가 끝난 뒤 업데이트하세요.')); return;
  }
  await updateAction('updateApply');
}
