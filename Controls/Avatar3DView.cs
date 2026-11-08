using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using RoDeck.Services;

namespace RoDeck.Controls;

/// <summary>
/// Shows an <see cref="Avatar3D"/> you can turn: drag left / right to spin it (see the back), drag up / down to tilt a little,
/// scroll to zoom, double-click to reset. It turns slowly by itself until you touch it.
/// </summary>
public class Avatar3DView : Border
{
    public static readonly DependencyProperty AvatarProperty = DependencyProperty.Register(
        nameof(Avatar), typeof(Avatar3D), typeof(Avatar3DView), new PropertyMetadata(null, (d, _) => ((Avatar3DView)d).Show()));

    public Avatar3D? Avatar { get => (Avatar3D?)GetValue(AvatarProperty); set => SetValue(AvatarProperty, value); }

    private readonly Viewport3D _view = new() { ClipToBounds = true, IsHitTestVisible = false };
    private readonly PerspectiveCamera _camera = new() { FieldOfView = 30, UpDirection = new Vector3D(0, 1, 0) };
    private readonly ModelVisual3D _holder = new();
    private readonly AxisAngleRotation3D _spin = new(new Vector3D(0, 1, 0), 180);   // Roblox models face -Z: start facing us
    private readonly AxisAngleRotation3D _tilt = new(new Vector3D(1, 0, 0), 0);
    private double _zoom = 1;
    private Point? _dragFrom;
    private bool _touched;

    public Avatar3DView()
    {
        Background = Brushes.Transparent;   // so drags anywhere in the box count
        Cursor = Cursors.SizeWE;
        ToolTip = "Drag to turn · scroll to zoom · double-click to reset";
        _view.Camera = _camera;

        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(150, 150, 150)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(150, 150, 150), new Vector3D(-0.4, -0.6, -1)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(70, 70, 70), new Vector3D(0.6, -0.2, 1)));
        _view.Children.Add(new ModelVisual3D { Content = lights });
        _view.Children.Add(_holder);
        Child = _view;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { Reset(); return; }
            _dragFrom = e.GetPosition(this);
            _touched = true;
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, _) => { _dragFrom = null; ReleaseMouseCapture(); };
        MouseMove += (_, e) =>
        {
            if (_dragFrom is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            _spin.Angle += (now.X - from.X) * 0.6;
            _tilt.Angle = Math.Clamp(_tilt.Angle + (now.Y - from.Y) * 0.3, -25, 25);
            _dragFrom = now;
        };
        MouseWheel += (_, e) =>
        {
            _zoom = Math.Clamp(_zoom * (e.Delta > 0 ? 0.9 : 1.1), 0.5, 1.8);
            PlaceCamera();
            e.Handled = true;   // zoom the model, don't scroll the page
        };

        // a slow turn until the person drags it themselves
        CompositionTarget.Rendering += (_, _) =>
        {
            if (!_touched && IsVisible && Avatar != null) _spin.Angle += 0.25;
        };
    }

    private void Show()
    {
        var a = Avatar;
        if (a == null) { _holder.Content = null; return; }
        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(-a.Center.X, -a.Center.Y, -a.Center.Z));
        transform.Children.Add(new RotateTransform3D(_spin));
        transform.Children.Add(new RotateTransform3D(_tilt));
        _holder.Content = a.Model;
        _holder.Transform = transform;
        PlaceCamera();
    }

    private void PlaceCamera()
    {
        double size = Avatar?.Size ?? 6;
        double distance = size / (2 * Math.Tan(_camera.FieldOfView * Math.PI / 360)) * 1.15 * _zoom;
        _camera.Position = new Point3D(0, 0, distance);
        _camera.LookDirection = new Vector3D(0, 0, -distance);
    }

    private void Reset()
    {
        _spin.Angle = 180;
        _tilt.Angle = 0;
        _zoom = 1;
        _touched = false;
        PlaceCamera();
    }
}
