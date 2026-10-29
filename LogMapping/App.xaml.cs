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
            new MainWindow().Show();
        }
    }
}
