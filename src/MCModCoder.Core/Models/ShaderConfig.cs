namespace MCModCoder.Core.Models;

/// <summary>光影（着色器）配置 —— 光源 / 阴影 / 环境光 / 雾效</summary>
public class ShaderConfig
{
    public string Name { get; set; } = "MyShader";
    // 光源
    public double SunAngle { get; set; } = 45;        // 太阳高度角 0-90
    public double SunAzimuth { get; set; } = 135;     // 方位角 0-360
    public double SunIntensity { get; set; } = 1.0;
    public string SunColor { get; set; } = "#FFF3D6";
    // 阴影
    public double ShadowStrength { get; set; } = 0.6; // 0-1
    public double ShadowSoftness { get; set; } = 0.5;
    // 环境光
    public double AmbientIntensity { get; set; } = 0.35;
    public string AmbientColor { get; set; } = "#8FA6C8";
    // 雾效
    public double FogDensity { get; set; } = 0.015;
    public string FogColor { get; set; } = "#A8C4E0";
    public double FogHeightFalloff { get; set; } = 0.5;
    // 其他
    public double EmissiveBoost { get; set; } = 1.0;
    public string WaterTint { get; set; } = "#3F76E4";
    public bool VolumetricLight { get; set; } = false;
    public bool TonemappingACES { get; set; } = true;

    /// <summary>导出为 GLSL 风格 uniforms 代码（供光影包使用）</summary>
    public string ToGlsl()
    {
        double az = SunAzimuth * Math.PI / 180.0, el = SunAngle * Math.PI / 180.0;
        double sx = Math.Cos(el) * Math.Sin(az), sy = Math.Sin(el), sz = Math.Cos(el) * Math.Cos(az);
        return $$"""
// {{Name}} — 由 MC模组代码工作台 光影编辑器生成
// 顶点着色器与片元着色器共用 uniforms
uniform vec3  sunDirection   = vec3({{sx:F4}}, {{sy:F4}}, {{sz:F4}});
uniform float sunIntensity   = {{SunIntensity:F2}};
uniform vec3  sunColor       = {{GlslVec(SunColor)}};
uniform float shadowStrength = {{ShadowStrength:F2}};
uniform float shadowSoftness = {{ShadowSoftness:F2}};
uniform float ambientIntensity = {{AmbientIntensity:F2}};
uniform vec3  ambientColor   = {{GlslVec(AmbientColor)}};
uniform float fogDensity     = {{FogDensity:F4}};
uniform vec3  fogColor       = {{GlslVec(FogColor)}};
uniform float fogHeightFalloff = {{FogHeightFalloff:F2}};
uniform float emissiveBoost  = {{EmissiveBoost:F2}};
uniform vec3  waterTint      = {{GlslVec(WaterTint)}};
uniform bool  volumetricLight = {{(VolumetricLight ? "true" : "false").ToLower()}};
uniform bool  tonemapACES    = {{(TonemappingACES ? "true" : "false").ToLower()}};

vec3 applyFog(vec3 color, float dist, float worldY) {
    float f = 1.0 - exp(-fogDensity * dist * exp(-fogHeightFalloff * max(worldY, 0.0)));
    return mix(color, fogColor, clamp(f, 0.0, 1.0));
}

vec3 applyLighting(vec3 albedo, float ndl, float shadow) {
    vec3 direct = sunColor * sunIntensity * max(ndl, 0.0) * (1.0 - shadow * shadowStrength);
    vec3 ambient = ambientColor * ambientIntensity;
    return albedo * (direct + ambient);
}
""";
    }

    private static string GlslVec(string hex)
    {
        var (r, g, b) = ColorUtil.ParseHex(hex);
        return $"vec3({r / 255.0:F3}, {g / 255.0:F3}, {b / 255.0:F3})";
    }
}
