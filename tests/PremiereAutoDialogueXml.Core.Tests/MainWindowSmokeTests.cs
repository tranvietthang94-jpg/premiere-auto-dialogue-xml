using System.Runtime.ExceptionServices;
using System.Windows.Threading;
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
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    try
                    {
                        var window = new MainWindow
                        {
                            ShowInTaskbar = false,
                            WindowState = System.Windows.WindowState.Minimized
                        };
                        window.Show();
                        window.Close();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }
                });
                Dispatcher.Run();
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(30)), "Cửa sổ WPF không mở/đóng trong 30 giây.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
