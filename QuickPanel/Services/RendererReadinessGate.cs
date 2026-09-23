using System;

namespace QuickPanel.Services;

internal sealed class RendererReadinessGate
{
	private readonly int _requiredStableSamples;

	private nint _windowHandle;

	private nint _rendererHandle;

	private int _width;

	private int _height;

	private int _stableSamples;

	public RendererReadinessGate(int requiredStableSamples)
	{
		if (requiredStableSamples < 1)
		{
			throw new ArgumentOutOfRangeException(nameof(requiredStableSamples));
		}
		_requiredStableSamples = requiredStableSamples;
	}

	public bool Observe(nint windowHandle, nint rendererHandle, int width, int height, bool isResponsive)
	{
		if (windowHandle == IntPtr.Zero || rendererHandle == IntPtr.Zero || width <= 100 || height <= 100 || !isResponsive)
		{
			Reset();
			return false;
		}
		if (_windowHandle != windowHandle || _rendererHandle != rendererHandle || _width != width || _height != height)
		{
			_windowHandle = windowHandle;
			_rendererHandle = rendererHandle;
			_width = width;
			_height = height;
			_stableSamples = 1;
			return _requiredStableSamples == 1;
		}
		_stableSamples++;
		return _stableSamples >= _requiredStableSamples;
	}

	public void Reset()
	{
		_windowHandle = IntPtr.Zero;
		_rendererHandle = IntPtr.Zero;
		_width = 0;
		_height = 0;
		_stableSamples = 0;
	}
}
