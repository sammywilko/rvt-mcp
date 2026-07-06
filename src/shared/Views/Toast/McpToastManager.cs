using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RvtMcp.Plugin.Views.Toast
{
    internal sealed class McpToastManager
    {
        private const int MaxToasts = 4;
        private const double Gap = 8;
        private const double EdgeMargin = 16;

        private readonly Dispatcher _dispatcher;
        private readonly List<McpToastWindow> _active = new List<McpToastWindow>();
        private IntPtr _ownerHandle;

        public McpToastManager(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void SetOwnerHandle(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero)
                _ownerHandle = hwnd;
        }

        public void Complete(McpToastViewModel vm)
        {
            EnsureDispatcher();

            EnforceCapBeforeAdd();
            var window = new McpToastWindow(vm, OnToastClosed);
            _active.Insert(0, window);
            window.Show();
            window.PlayEnterAnimation();
            window.StartAutoDismiss();
            ReflowAll(animate: false);
        }

        public void DismissAllImmediate()
        {
            EnsureDispatcher();

            var toasts = _active.ToList();
            _active.Clear();
            foreach (var toast in toasts)
                toast.CloseImmediate();
        }

        private void EnforceCapBeforeAdd()
        {
            while (_active.Count >= MaxToasts)
            {
                var oldest = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                oldest.CloseImmediate();
            }
        }

        private void OnToastClosed(McpToastWindow toast)
        {
            if (_active.Remove(toast))
                ReflowAll(animate: true);
        }

        private void ReflowAll(bool animate)
        {
            var owner = GetValidOwnerHandle();
            double startTop = EdgeMargin;
            double startLeft = EdgeMargin;

            if (owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
            {
                GetDpiScale(out var dpiX, out var dpiY);
                startLeft = rect.Left * dpiX + EdgeMargin;
                startTop = rect.Top * dpiY + EdgeMargin;
            }

            var currentTop = startTop;
            foreach (var toast in _active)
            {
                var height = toast.ActualHeight > 0 ? toast.ActualHeight : 72;

                if (animate)
                    toast.AnimateToPosition(currentTop, startLeft);
                else
                {
                    toast.Top = currentTop;
                    toast.Left = startLeft;
                }

                currentTop += height + Gap;
            }
        }

        private IntPtr GetValidOwnerHandle()
        {
            if (_ownerHandle == IntPtr.Zero || !IsWindow(_ownerHandle))
                _ownerHandle = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            return _ownerHandle;
        }

        private void GetDpiScale(out double dpiX, out double dpiY)
        {
            dpiX = 1.0;
            dpiY = 1.0;

            var first = _active.FirstOrDefault();
            if (first == null)
                return;

            var source = System.Windows.PresentationSource.FromVisual(first);
            if (source?.CompositionTarget != null)
            {
                var transform = source.CompositionTarget.TransformFromDevice;
                dpiX = transform.M11;
                dpiY = transform.M22;
            }
        }

        private void EnsureDispatcher()
        {
            if (!_dispatcher.CheckAccess())
                throw new InvalidOperationException("McpToastManager must run on the toast dispatcher thread.");
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }
    }
}
