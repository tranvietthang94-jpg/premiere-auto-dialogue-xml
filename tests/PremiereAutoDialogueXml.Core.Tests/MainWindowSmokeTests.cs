using System.Runtime.ExceptionServices;
using PremiereAutoDialogueXml.App;

namespace PremiereAutoDialogueXml.Core.Tests;

[TestClass]
public sealed class MainWindowSmokeTests
{
    [TestMethod]
    public void MainWindowCanShowAndCloseOnStaThread()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                var window = new MainWindow
                {
                    ShowInTaskbar = false,
                    WindowState = System.Windows.WindowState.Minimized
                };
                window.Show();
                window.Dispatcher.Invoke(() => { });
                window.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(10)), "Cửa sổ WPF không mở/đóng trong 10 giây.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
