using System;
using System.IO;
using System.Windows;

namespace MdView
{
    public partial class App : Application
    {
        private int _windowCount;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.Initialize();
            var file = e.Args.Length > 0 && File.Exists(e.Args[0]) ? e.Args[0] : null;
            CreateWindow(file);
        }

        public void CreateWindow(string? filePath = null)
        {
            _windowCount++;
            var win = new MainWindow();
            win.Closed += (_, _) =>
            {
                _windowCount--;
                if (_windowCount <= 0)
                    Shutdown();
            };
            win.Show();
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
                win.OpenFile(filePath);
        }
    }
}
