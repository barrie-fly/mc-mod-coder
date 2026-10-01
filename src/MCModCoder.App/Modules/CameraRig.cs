using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace MCModCoder.Modules;

/// <summary>
/// 轨道相机装置：球坐标（theta/phi）+ 距离 + 注视点。
/// 预设切换走缓动动画（cubic in-out 逐帧插值），自由模式支持旋转/平移/缩放；
/// 任何手动操作会取消进行中的动画并触发 <see cref="UserInteracted"/>（用于自动切到自由模式）。
/// </summary>
public sealed class CameraRig
{
    private readonly PerspectiveCamera _cam;

    public double Theta = Math.PI / 4;
    public double Phi = Math.PI / 4.4;
    public double Distance = 26;
    public Point3D Target;

    public const double MinDistance = 4, MaxDistance = 120;
    public const double MinPhi = 0.06, MaxPhi = Math.PI - 0.06;

    /// <summary>用户手动旋转/平移/缩放时触发（视角预设面板据此自动切到"自由"）</summary>
    public event Action? UserInteracted;

    // ---- 动画状态 ----
    private (double th, double ph, double dist, double tx, double ty, double tz) _from, _to;
    private TimeSpan _duration = TimeSpan.FromMilliseconds(500);
    private DateTime _startTime;
    private bool _animating;

    public CameraRig(Viewport3D viewport, Point3D initialTarget)
    {
        Target = initialTarget;
        _cam = new PerspectiveCamera
        {
            FieldOfView = 55,
            UpDirection = new Vector3D(0, 1, 0)
        };
        viewport.Camera = _cam;
        Apply();

        CompositionTarget_RenderingHook();
    }

    private void CompositionTarget_RenderingHook()
    {
        CompositionTarget.Rendering += (_, e) =>
        {
            if (!_animating) return;
            var (th, ph, dist, tx, ty, tz) = _to;
            double t = _duration.TotalMilliseconds <= 0
                ? 1
                : ((DateTime.UtcNow - _startTime).TotalMilliseconds / _duration.TotalMilliseconds);
            if (t >= 1)
            {
                _animating = false;
            }
            else
            {
                double k = EaseInOut(t);
                // theta 走最短角路径
                double dth = th - _from.th;
                dth = ((dth + Math.PI) % (2 * Math.PI) + 2 * Math.PI) % (2 * Math.PI) - Math.PI;
                th = _from.th + dth * k;
                ph = _from.ph + (ph - _from.ph) * k;
                dist = _from.dist + (dist - _from.dist) * k;
                tx = _from.tx + (tx - _from.tx) * k;
                ty = _from.ty + (ty - _from.ty) * k;
                tz = _from.tz + (tz - _from.tz) * k;
            }
            Theta = th; Phi = ph; Distance = dist;
            Target = new Point3D(tx, ty, tz);
            Apply();
        };
    }

    /// <summary>cubic in-out 缓动</summary>
    private static double EaseInOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    /// <summary>动画过渡到指定视角参数；若已在目标附近则直接落位</summary>
    public void AnimateTo(double theta, double phi, double distance, Point3D target, int durationMs = 500)
    {
        phi = Math.Clamp(phi, MinPhi, MaxPhi);
        distance = Math.Clamp(distance, MinDistance, MaxDistance);

        double dth = Math.Abs(NormAngle(theta - Theta));
        double dph = Math.Abs(phi - Phi);
        bool nearTarget = dth < 0.005 && dph < 0.005 &&
                          Math.Abs(distance - Distance) < 0.05 &&
                          (Target - target).Length < 0.05;
        if (nearTarget)
        {
            _animating = false;
            Theta = theta; Phi = phi; Distance = distance; Target = target;
            Apply();
            return;
        }

        _from = (Theta, Phi, Distance, Target.X, Target.Y, Target.Z);
        _to = (theta, phi, distance, target.X, target.Y, target.Z);
        _duration = TimeSpan.FromMilliseconds(Math.Clamp(durationMs, 120, 1200));
        _startTime = DateTime.UtcNow;
        _animating = true;
    }

    /// <summary>停止当前动画（保留当前画面状态）</summary>
    public void StopAnimation() => _animating = false;

    /// <summary>立即落位（不动画），用于初始状态</summary>
    public void JumpTo(double theta, double phi, double distance, Point3D target)
    {
        _animating = false;
        Theta = NormAngle(theta);
        Phi = Math.Clamp(phi, MinPhi, MaxPhi);
        Distance = Math.Clamp(distance, MinDistance, MaxDistance);
        Target = target;
        Apply();
    }

    /// <summary>手动旋转（拖拽增量）</summary>
    public void Orbit(double dTheta, double dPhi)
    {
        _animating = false;
        Theta = NormAngle(Theta + dTheta);
        Phi = Math.Clamp(Phi + dPhi, MinPhi, MaxPhi);
        Apply();
        UserInteracted?.Invoke();
    }

    /// <summary>手动平移：按相机右/上基向量移动注视点（dy 取反符合拖拽直觉）</summary>
    public void Pan(double dx, double dy)
    {
        _animating = false;
        var look = new Vector3D(Target.X - _cam.Position.X, Target.Y - _cam.Position.Y, Target.Z - _cam.Position.Z);
        if (look.LengthSquared < 1e-6) return;
        look.Normalize();
        var right = Vector3D.CrossProduct(look, new Vector3D(0, 1, 0));
        if (right.LengthSquared < 1e-6) right = new Vector3D(1, 0, 0);
        right.Normalize();
        var camUp = Vector3D.CrossProduct(right, look);
        camUp.Normalize();

        double scale = Math.Max(Distance, 6) * 0.0016;
        var t = Target + (-dx * right + dy * camUp) * scale;
        // 钳制注视点，防止拖丢场景
        Target = new Point3D(Math.Clamp(t.X, -32, 96), Math.Clamp(t.Y, -16, 80), Math.Clamp(t.Z, -32, 96));
        Apply();
        UserInteracted?.Invoke();
    }

    /// <summary>滚轮缩放</summary>
    public void Zoom(double delta)
    {
        _animating = false;
        Distance = Math.Clamp(Distance - delta, MinDistance, MaxDistance);
        Apply();
        UserInteracted?.Invoke();
    }

    /// <summary>由球坐标写回相机</summary>
    private void Apply()
    {
        double phi = Math.Clamp(Phi, MinPhi, MaxPhi);
        var pos = new Point3D(
            Target.X + Distance * Math.Cos(phi) * Math.Sin(Theta),
            Target.Y + Distance * Math.Sin(phi),
            Target.Z + Distance * Math.Cos(phi) * Math.Cos(Theta));
        _cam.Position = pos;
        _cam.LookDirection = new Vector3D(Target.X - pos.X, Target.Y - pos.Y, Target.Z - pos.Z);
        _cam.UpDirection = new Vector3D(0, 1, 0);
    }

    /// <summary>角度归一化到 (-π, π]</summary>
    private static double NormAngle(double a)
    {
        a %= 2 * Math.PI;
        if (a > Math.PI) a -= 2 * Math.PI;
        if (a <= -Math.PI) a += 2 * Math.PI;
        return a;
    }
}
