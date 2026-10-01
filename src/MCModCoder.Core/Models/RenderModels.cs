namespace MCModCoder.Core.Models;

/// <summary>粒子发射器配置</summary>
public class ParticleConfig
{
    public string Name { get; set; } = "MyEffect";
    public double RatePerSecond { get; set; } = 40;
    public double Lifetime { get; set; } = 1.2;        // 秒
    public double Speed { get; set; } = 1.0;
    public double Gravity { get; set; } = 0.4;
    public double Spread { get; set; } = 0.6;          // 发射锥半角 (0-1)
    public string ColorFrom { get; set; } = "#FFE066";
    public string ColorTo { get; set; } = "#FF5722";
    public double SizeStart { get; set; } = 4.0;       // 像素（预览用）
    public double SizeEnd { get; set; } = 0.5;
    public bool Additive { get; set; } = true;
    public int MaxCount { get; set; } = 600;
    /// <summary>发射形状：point / cone / ring</summary>
    public string Shape { get; set; } = "cone";
}

/// <summary>材质渲染参数</summary>
public class MaterialConfig
{
    public string Name { get; set; } = "MyMaterial";
    public double Metalness { get; set; } = 0.0;
    public double Roughness { get; set; } = 0.8;
    public double Emission { get; set; } = 0.0;
    public string BaseColor { get; set; } = "#C8C8C8";
    public bool UseNormalMap { get; set; } = true;
    public bool UseSpecularMap { get; set; } = false;
    public string RenderLayer { get; set; } = "solid";  // solid / cutout / translucent
}

/// <summary>动画曲线关键帧</summary>
public class AnimKeyframe
{
    public double Time { get; set; }      // 0-1
    public double Value { get; set; }     // 0-1
}

/// <summary>动画配置（粒子尺寸/透明度等曲线）</summary>
public class AnimationConfig
{
    public string Name { get; set; } = "SizeOverLife";
    public List<AnimKeyframe> Keys { get; set; } = new()
    {
        new() { Time = 0, Value = 1 },
        new() { Time = 0.4, Value = 0.8 },
        new() { Time = 1, Value = 0 },
    };

    public double Sample(double t)
    {
        if (Keys.Count == 0) return 1;
        if (t <= Keys[0].Time) return Keys[0].Value;
        for (int i = 1; i < Keys.Count; i++)
        {
            if (t <= Keys[i].Time)
            {
                var a = Keys[i - 1]; var b = Keys[i];
                double f = (t - a.Time) / Math.Max(1e-6, b.Time - a.Time);
                return a.Value + (b.Value - a.Value) * f;
            }
        }
        return Keys[^1].Value;
    }
}
