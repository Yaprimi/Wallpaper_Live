using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WallpaperMusicPlayer
{
    public partial class App : Application
    {
        // Іменований Mutex — унікальний для цього застосунку.
        // static щоб GC не зібрав його до завершення програми.
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            // Виставляємо SoftwareOnly ДО створення будь-якого вікна —
            // щоб WPF ніколи не створював DirectX swap chain.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

            const string mutexName = "Global\\WallpaperLiveMusicPlayer_SingleInstance";

            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                name: mutexName,
                out bool createdNew);

            if (!createdNew)
            {
                // Wallpaper Engine може запускати кілька екземплярів —
                // тихо завершуємось без будь-яких діалогів
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
                Shutdown();
                return;
            }

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Звільняємо Mutex при завершенні — дозволяємо наступному запуску захопити його
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }

            base.OnExit(e);
        }
    }
}