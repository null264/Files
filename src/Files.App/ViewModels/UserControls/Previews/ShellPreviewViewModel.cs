// Copyright (c) Files Community
// Licensed under the MIT License.

using Files.App.Helpers;
using Files.App.ViewModels.Properties;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectComposition;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT;
using WNDPROC = Windows.Win32.Extras.ManagedWNDPROC;

#pragma warning disable CS8305 // Type is for evaluation purposes only and is subject to change or removal in future updates.

namespace Files.App.ViewModels.Previews
{
	public sealed partial class ShellPreviewViewModel : BasePreviewModel
	{
		// Only one preview handler is created at a time: scrolling through files can otherwise start several
		// document applications at once, which is what makes a laptop crawl.
		static readonly SemaphoreSlim HandlerCreationGate = new(1, 1);

		// A large document can hold the gate for a long time, so a later preview does not wait for it forever.
		static readonly TimeSpan HandlerCreationGateTimeout = TimeSpan.FromSeconds(10);

		// Fields

		ContentExternalOutputLink? _contentExternalOutputLink;
		PreviewHandler? _previewHandler;
		ShellPreviewHost? _host;
		ID3D11Device? _d3d11Device;
		ID3D11DeviceContext? _d3d11DeviceContext;
		IDCompositionDevice? _dCompositionDevice;
		IDCompositionVisual? _childVisual;
		object? _controlSurface;
		WNDCLASSEXW _windowClass;
		WNDPROC _windProc = null!;
		HWND _hWnd = HWND.Null;
		volatile bool _isHostedByDocumentApplication = false;
		volatile bool _unloaded;
		Task? _unloadTask;
		readonly object _unloadGate = new();
		unsafe char* _pszClassName;

		// Constructor

		public ShellPreviewViewModel(ListedItem item) : base(item)
		{
		}

		// Methods

		public override Task<List<FileProperty>> LoadPreviewAndDetailsAsync()
			=> Task.FromResult<List<FileProperty>>([]);

		public static unsafe Guid? FindPreviewHandlerFor(string extension, nint hwnd)
		{
			if (string.IsNullOrEmpty(extension))
				return null;

			try
			{
				fixed (char* pszAssoc = extension,
					pszExtra = "{8895b1c6-b41f-4c1c-a562-0d564250836f}",
					pszOutput = new char[1024])
				{
					PWSTR pwszAssoc = new(pszAssoc);
					PWSTR pwszExtra = new(pszExtra);
					PWSTR pwszOutput = new(pszOutput);
					uint cchOutput = 2024;

					// Try to find registered preview handler associated with specified extension name
					var res = PInvoke.AssocQueryString(
						ASSOCF.ASSOCF_NOTRUNCATE,
						ASSOCSTR.ASSOCSTR_SHELLEXTENSION,
						pwszAssoc,
						pwszExtra,
						pwszOutput,
						&cchOutput);

					return Guid.Parse(pwszOutput.ToString());
				}
			}
			catch
			{
				return null;
			}
		}

		public void SizeChanged(RECT size)
		{
			if (_hWnd != HWND.Null)
				PInvoke.SetWindowPos(_hWnd, new(0), size.left, size.top, size.Width, size.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

			PostToHost(() => _previewHandler?.ResetBounds(new(0, 0, size.Width, size.Height)));

			if (_contentExternalOutputLink is not null)
				_contentExternalOutputLink.PlacementVisual.Size = new(size.Width, size.Height);
		}

		private unsafe LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam)
		{
			// This window procedure runs on the UI thread, synchronously inside CreateWindowEx for WM_CREATE.
			// Creating the preview handler is a blocking, cross-process call chain (for Office files it starts
			// Word/Excel and parses the document), so it is queued to the preview host thread by LoadPreview
			// instead of being done here.
			return PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);
		}

		public unsafe void LoadPreview(UIElement presenter)
		{
			App.Logger.LogInformation($"ShellPreview.LoadPreview: Item={LogPathHelper.RedactPath(Item?.ItemPath)}");

			var parent = MainWindow.Instance.WindowHandle;
			var hInst = PInvoke.GetModuleHandle(default(PWSTR));
			var szClassName = $"{nameof(ShellPreviewViewModel)}-{Guid.NewGuid()}";
			var szWindowName = $"Preview";

			_pszClassName = (char*)Marshal.StringToHGlobalUni(szClassName);

			_windProc = new(WndProc);
			var pWindProc = Marshal.GetFunctionPointerForDelegate<WNDPROC>(_windProc);
			var pfnWndProc = (delegate* unmanaged[Stdcall]<HWND, uint, WPARAM, LPARAM, LRESULT>)pWindProc;

			_windowClass = new WNDCLASSEXW()
			{
				cbSize = (uint)sizeof(WNDCLASSEXW),
				lpfnWndProc = pfnWndProc,
				hInstance = hInst,
				lpszClassName = _pszClassName,
				style = 0,
				hIcon = default,
				hIconSm = default,
				hCursor = default,
				hbrBackground = default,
				lpszMenuName = null,
				cbClsExtra = 0,
				cbWndExtra = 0,
			};

			PInvoke.RegisterClassEx(_windowClass);

			fixed (char* pszWindowName = szWindowName)
			{
				_hWnd = PInvoke.CreateWindowEx(
					WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_COMPOSITED,
					_pszClassName,
					pszWindowName,
					WINDOW_STYLE.WS_CHILD | WINDOW_STYLE.WS_CLIPSIBLINGS | WINDOW_STYLE.WS_VISIBLE,
					0, 0, 0, 0,
					new(parent),
					HMENU.Null,
					hInst);
			}

			_ = ChildWindowToXaml(parent, presenter);

			// Attaching the handler is what used to block the UI thread, so it runs on its own STA thread.
			var extension = Item?.FileExtension ?? string.Empty;
			_host = new ShellPreviewHost($"{nameof(ShellPreviewViewModel)}-{extension}");
			_ = AttachPreviewAsync();
		}

		private async Task AttachPreviewAsync()
		{
			var host = _host;
			if (host is null)
				return;

			var gateHeld = false;

			try
			{
				gateHeld = await HandlerCreationGate.WaitAsync(HandlerCreationGateTimeout);

				if (!gateHeld)
					App.Logger.LogInformation("ShellPreview.AttachPreview: no free handler slot, creating the handler alongside the previous one.");

				// Waiting for the gate may have taken long enough for this preview to be superseded.
				if (_unloaded)
					return;

				await host.Post(AttachPreviewOnHostThread);
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, $"ShellPreview.AttachPreview: Item={LogPathHelper.RedactPath(Item?.ItemPath)}");

				// Some handlers only work on the thread that owns their window and message pump. Running the
				// same steps on the UI thread is what this code did before the preview host thread existed:
				// slower, but it renders.
				AttachPreviewOnUiThread();
			}
			finally
			{
				if (gateHeld)
					HandlerCreationGate.Release();
			}
		}

		/// <summary>
		/// Last resort for a handler that failed on the preview host thread.
		/// </summary>
		private void AttachPreviewOnUiThread()
		{
			if (_unloaded)
				return;

			App.Logger.LogInformation($"ShellPreview.AttachPreview: retrying on the UI thread. Item={LogPathHelper.RedactPath(Item?.ItemPath)}");

			try
			{
				AttachPreviewOnHostThread();
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, $"ShellPreview.AttachPreview failed: Item={LogPathHelper.RedactPath(Item?.ItemPath)}");

				// Match the previous behavior: a handler that cannot be created leaves the pane empty.
				_ = MainWindow.Instance.DispatcherQueue.EnqueueOrInvokeAsync(UnloadPreview);
			}
		}

		private void AttachPreviewOnHostThread()
		{
			// Checkpoint 1: the selection may have moved on before this work item even started.
			if (_unloaded || _hWnd == HWND.Null)
				return;

			var itemPath = Item?.ItemPath;
			if (itemPath is null)
				return;

			var clsid = FindPreviewHandlerFor(Item?.FileExtension ?? string.Empty, (nint)_hWnd);
			if (clsid is null)
				return;

			// Whether this handler runs inside the application that opens the file type, which is what makes it
			// need the extended styles cleared below. Derived from the registration on the machine, so another
			// preview provider that hosts its own application gets the same treatment without code changes.
			_isHostedByDocumentApplication = ShellPreviewHandlerRegistration.IsDocumentApplicationHandler(clsid.Value, Item?.FileExtension);

			App.Logger.LogInformation($"ShellPreview.AttachPreview: handler={clsid}, hostedByDocumentApplication={_isHostedByDocumentApplication}, Item={LogPathHelper.RedactPath(itemPath)}");

			var stopwatch = Stopwatch.StartNew();

			try
			{
				// Starting the handler server (a Word/Excel process for Office files) is the first blocking step.
				_previewHandler = new PreviewHandler(clsid.Value, (nint)_hWnd);
				var createdMs = stopwatch.ElapsedMilliseconds;

				// Checkpoint 2: skip the expensive initialization when the preview is already gone.
				if (_unloaded)
				{
					DetachPreviewOnHostThread();
					return;
				}

				// Parsing the document happens here.
				_previewHandler.InitWithFileWithEveryWay(itemPath);
				var initializedMs = stopwatch.ElapsedMilliseconds;

				// Checkpoint 3: rendering is the most expensive step, so it is the one worth skipping.
				if (_unloaded)
				{
					App.Logger.LogInformation($"ShellPreview.AttachPreview: skipped rendering for a stale preview. Item={LogPathHelper.RedactPath(itemPath)}");
					DetachPreviewOnHostThread();
					return;
				}

				_previewHandler.DoPreview();

				App.Logger.LogInformation($"ShellPreview.AttachPreview: create={createdMs}ms init={initializedMs - createdMs}ms preview={stopwatch.ElapsedMilliseconds - initializedMs}ms Item={LogPathHelper.RedactPath(itemPath)}");
			}
			catch (Exception ex)
			{
				App.Logger.LogWarning(ex, $"ShellPreview.AttachPreview failed on the preview host: Item={LogPathHelper.RedactPath(itemPath)}");

				DetachPreviewOnHostThread();

				// Reported to the caller, which falls back to creating the handler on the UI thread.
				throw;
			}
		}

		private void DetachPreviewOnHostThread()
		{
			var handler = _previewHandler;
			_previewHandler = null;

			if (handler is not null)
			{
				try
				{
					handler.Dispose();
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, $"ShellPreview.DetachPreview: Item={LogPathHelper.RedactPath(Item?.ItemPath)}");
				}
			}
		}

		private void PostToHost(Action work)
		{
			var host = _host;
			if (host is null)
				return;

			try
			{
				// Fire and forget: the host thread owns these calls, failures are logged.
				host.Post(work).ContinueWith(
					static task => App.Logger.LogWarning(task.Exception, "ShellPreview: a host work item failed."),
					CancellationToken.None,
					TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
					TaskScheduler.Default);
			}
			catch (InvalidOperationException)
			{
				// The host was disposed concurrently, the preview is already gone.
			}
		}

		private unsafe bool ChildWindowToXaml(nint parent, UIElement presenter)
		{
			D3D_DRIVER_TYPE[] driverTypes =
			[
				D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
				D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP,
			];

			HRESULT hr = default;

			// Create the D3D11 device
			foreach (var driverType in driverTypes)
			{
				hr = PInvoke.D3D11CreateDevice(
					null!, driverType, new(nint.Zero),
					D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
					ReadOnlySpan<D3D_FEATURE_LEVEL>.Empty, /* SDKVersion */ 7,
					out _d3d11Device,
					out _d3d11DeviceContext);

				if (hr.Succeeded)
					break;
			}

			if (_d3d11Device is null)
				return false;

			// Create the DComp device
			var pDXGIDevice = (IDXGIDevice)_d3d11Device;
			hr = PInvoke.DCompositionCreateDevice(pDXGIDevice, out _dCompositionDevice);
			if (hr.Failed || _dCompositionDevice is null)
				return false;

			// Create the visual
			hr = _dCompositionDevice.CreateVisual(out _childVisual);
			if (hr.Failed)
				return false;

			hr = _dCompositionDevice.CreateSurfaceFromHwnd(_hWnd, out _controlSurface);
			if (hr.Failed)
				return false;

			hr = _childVisual.SetContent(_controlSurface);
			if (hr.Failed || _childVisual is null || _controlSurface is null)
				return false;

			// Get the compositor and set the visual on it
			var compositor = ElementCompositionPreview.GetElementVisual(presenter).Compositor;
			_contentExternalOutputLink = ContentExternalOutputLink.Create(compositor);

			var target = _contentExternalOutputLink.As<Windows.Win32.Extras.IDCompositionTarget>();
			target.SetRoot(_childVisual);

			_contentExternalOutputLink.PlacementVisual.Size = new(0, 0);
			_contentExternalOutputLink.PlacementVisual.Scale = new(1 / (float)presenter.XamlRoot.RasterizationScale);
			ElementCompositionPreview.SetElementChildVisual(presenter, _contentExternalOutputLink.PlacementVisual);

			// Commit the all pending DComp commands
			_dCompositionDevice.Commit();

			var dwAttrib = Convert.ToUInt32(true);

			return
				PInvoke.DwmSetWindowAttribute(
					new((nint)_hWnd),
					DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
					&dwAttrib,
					(uint)Marshal.SizeOf(dwAttrib))
				.Succeeded;
		}

		public void UnloadPreview()
			=> _ = UnloadPreviewAsync();

		/// <summary>
		/// Tears the preview down. Safe to call repeatedly: every caller observes the same teardown.
		/// </summary>
		public Task UnloadPreviewAsync()
		{
			lock (_unloadGate)
				return _unloadTask ??= UnloadPreviewAsyncCore();
		}

		private async Task UnloadPreviewAsyncCore()
		{
			// Called both when closing to background and from Unloaded, so ignore the second call
			if (_unloaded)
				return;
			_unloaded = true;

			// Release the handler on its own thread first. It has to happen there, and the FIFO host queue
			// guarantees it runs after an attach that is still in flight.
			var host = _host;
			_host = null;

			if (host is not null)
			{
				try
				{
					await host.Post(DetachPreviewOnHostThread).ConfigureAwait(true);
				}
				catch (Exception ex)
				{
					App.Logger.LogWarning(ex, $"ShellPreview.UnloadPreview: host teardown failed. Item={LogPathHelper.RedactPath(Item?.ItemPath)}");
				}

				host.Dispose();
			}

			var windowHandle = _hWnd;
			if (windowHandle != HWND.Null)
			{
				PInvoke.DestroyWindow(windowHandle);
				App.Logger.LogInformation($"ShellPreview.UnloadPreview: HWND={((nint)windowHandle)}, Item={LogPathHelper.RedactPath(Item?.ItemPath)}");
				_hWnd = HWND.Null;
			}
			else
				App.Logger.LogInformation($"ShellPreview.UnloadPreview: HWND=, Item={LogPathHelper.RedactPath(Item?.ItemPath)}");


			_contentExternalOutputLink?.Dispose();
			_contentExternalOutputLink = null;

			// Release the composition chain leaf-to-root so nothing is left for finalizer-thread teardown
			ReleaseComObject(ref _childVisual);
			ReleaseComObject(ref _controlSurface);
			ReleaseComObject(ref _dCompositionDevice);
			ReleaseComObject(ref _d3d11DeviceContext);
			ReleaseComObject(ref _d3d11Device);

			PInvoke.UnregisterClass(_windowClass.lpszClassName, PInvoke.GetModuleHandle(default(PWSTR)));

			FreeWindowClassName();
		}

		private unsafe void FreeWindowClassName()
		{
			if (_pszClassName is not null)
			{
				Marshal.FreeHGlobal((nint)_pszClassName);
				_pszClassName = null;
			}
		}

		private static void ReleaseComObject<T>(ref T? comInterface) where T : class
		{
			if ((object?)comInterface is ComObject comObject)
				comObject.FinalRelease();

			comInterface = null;
		}

		public unsafe void PointerEntered(bool onPreview)
		{
			if (onPreview)
			{
				var dwAttrib = Convert.ToUInt32(false);

				PInvoke.DwmSetWindowAttribute(
					new((nint)_hWnd),
					DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
					&dwAttrib,
					(uint)Marshal.SizeOf(dwAttrib));

				if (_isHostedByDocumentApplication)
					PInvoke.SetWindowLongPtr(new((nint)_hWnd), WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, 0);
			}
			else
			{
				PInvoke.SetWindowLongPtr(
					new((nint)_hWnd),
					WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
					(nint)(WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_COMPOSITED));

				var dwAttrib = Convert.ToUInt32(true);

				PInvoke.DwmSetWindowAttribute(
					new((nint)_hWnd),
					DWMWINDOWATTRIBUTE.DWMWA_CLOAK,
					&dwAttrib,
					(uint)Marshal.SizeOf(dwAttrib));
			}
		}
	}
}

#pragma warning restore CS8305 // Type is for evaluation purposes only and is subject to change or removal in future updates.
