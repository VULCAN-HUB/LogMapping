using System.Windows;

namespace LogMapping
{
    public partial class App : Application
    {
        internal static string? UpdateReadyPipe;
        private async void OnStartup(object sender, StartupEventArgs e)
        {
            if(e.Args.Length == 3 && e.Args[0] == "--apply-update")
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                try { await PortableUpdate.RunHelperAsync(e.Args[1], e.Args[2]); Shutdown(0); }
                catch (System.Exception ex) { MessageBox.Show("업데이트를 완료하지 못했습니다. 기존 실행 파일과 백업을 보존했습니다.\n"+ex.Message,"LogMapping 업데이트"); Shutdown(1); }
                return;
            }
            if(e.Args.Length == 2 && e.Args[0] == "--update-ready") UpdateReadyPipe=e.Args[1];
            // 관리자 권한으로 다시 시작: 앞선 창이 끝난 뒤에 띄운다(권한이 다른 두 창은 WebView2 자료 폴더를 함께 못 쓴다).
            if(e.Args.Length == 2 && e.Args[0] == "--wait-exit" && int.TryParse(e.Args[1], out int parent))
                try { using var p = System.Diagnostics.Process.GetProcessById(parent); await p.WaitForExitAsync(new System.Threading.CancellationTokenSource(15000).Token); } catch { }
            new MainWindow().Show();
        }
    }
}
