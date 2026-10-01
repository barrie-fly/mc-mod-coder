using MCModCoder.Core.Models;

namespace MCModCoder.Core.Services;

/// <summary>项目脚手架生成器：按 MC 版本与加载器生成完整可构建工程</summary>
public static class TemplateGenerator
{
    /// <summary>Fabric Loader 兜底版本（版本矩阵未指定时使用）</summary>
    private const string DefaultFabricLoader = "0.16.10";

    public static List<string> Generate(McProject p)
    {
        var written = new List<string>();
        var root = p.ProjectDir;
        Directory.CreateDirectory(root);
        var javaRoot = Path.Combine(root, "src", "main", "java");
        var pkgDir = Path.Combine(javaRoot, p.JavaPackagePath);
        Directory.CreateDirectory(pkgDir);
        var resourcesDir = Path.Combine(root, "src", "main", "resources");
        Directory.CreateDirectory(resourcesDir);   // 修复：Rift 模板不写资源文件时 pack.mcmeta 会因目录缺失而崩溃

        var ver = McVersions.Find(p.McVersion) ?? McVersions.Supported[^1];
        string mainClass = p.MainClass;
        string modid = SanitizeModId(p.ModId);

        switch (p.ModLoader)
        {
            case "Fabric":
                GenerateFabric(p, root, pkgDir, mainClass, modid, ver, written);
                break;
            case "Forge" when p.McVersion == "1.12.2":
                GenerateForge112(p, root, pkgDir, mainClass, modid, written);
                break;
            case "Forge":
                GenerateForgeModern(p, root, pkgDir, mainClass, modid, ver, written);
                break;
            case "NeoForge":
                GenerateNeoForge(p, root, pkgDir, mainClass, modid, ver, written);
                break;
            default: // Rift (1.12.2 轻量加载器)
                GenerateFabricLike(p, root, pkgDir, mainClass, modid, written);
                break;
        }

        // pack.mcmeta（资源包）
        File.WriteAllText(Path.Combine(resourcesDir, "pack.mcmeta"),
            $$"""{ "pack": { "pack_format": {{ver.PackFormat}}, "description": "{{p.Name}} 资源" } }""");
        written.Add("src/main/resources/pack.mcmeta");

        // 工作台元数据
        File.WriteAllText(p.ProjectFilePath,
            System.Text.Json.JsonSerializer.Serialize(p, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        written.Add(Path.GetFileName(p.ProjectFilePath));

        // .gitignore（版本控制接口预留）
        File.WriteAllText(Path.Combine(root, ".gitignore"),
            "build/\n.gradle/\nrun/\nout/\n.idea/\n*.iml\n.eclipse/\n*.launch\n");
        written.Add(".gitignore");

        return written;
    }

    private static void Write(string root, string rel, string content, List<string> written)
    {
        var path = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        written.Add(rel);
    }

    /// <summary>净化模组 ID：仅保留小写字母/数字/下划线，保证可安全用于目录名与构建脚本</summary>
    private static string SanitizeModId(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in (string.IsNullOrWhiteSpace(s) ? "mymod" : s).ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        var r = sb.ToString().Trim('_');
        if (r.Length == 0) r = "mymod";
        if (char.IsDigit(r[0])) r = "_" + r;
        return r.Length > 64 ? r[..64] : r;
    }

    /// <summary>版本主版本号（1.20.1 → 20，26.3 → 26）</summary>
    private static string MajorVersion(McVersionInfo ver)
    {
        var idx = ver.Version.IndexOf('.');
        return idx > 0 ? ver.Version[..idx] : ver.Version;
    }

    // ---------- Forge 1.13+ ----------
    private static void GenerateForgeModern(McProject p, string root, string pkgDir, string mainClass, string modid, McVersionInfo ver, List<string> written)
    {
        string forgeVer = string.IsNullOrEmpty(ver.ForgeVersion) ? "47.3.0" : ver.ForgeVersion;
        string loaderMajor = forgeVer.Split('.')[0];   // FML loaderVersion 与 Forge 主版本一致，如 [47,)
        // 映射通道：1.17+ 使用官方映射，旧版本使用 snapshot（版本号解析容错）
        var verSegs = ver.Version.Split('.');
        int minorVer = verSegs.Length > 1 && int.TryParse(verSegs[1], out var mv) ? mv : 0;
        string mappingChannel = minorVer >= 17 ? "official" : "snapshot";

        Write(root, "build.gradle", $$"""
plugins {
    id 'eclipse'
    id 'idea'
    id 'net.minecraftforge.gradle' version '6.0.+'
}

version = '1.0.0'
group = '{{p.Package}}'
base { archivesName = '{{modid}}' }

java.toolchain.languageVersion = JavaLanguageVersion.of({{ver.JavaVersion}})

minecraft {
    mappings channel: '{{mappingChannel}}', version: '{{p.McVersion}}'
    runs {
        client { workingDirectory project.file('run') }
        server { workingDirectory project.file('run') }
    }
}

dependencies {
    minecraft "net.minecraftforge:forge:{{p.McVersion}}-{{forgeVer}}"
{{(p.Dependencies.Any() ? string.Join("\n", p.Dependencies.Select(d => $"    {d.Scope} \"{d.Coordinate}\"")) : "")}}
}

tasks.withType(ProcessResources).configureEach {
    duplicatesStrategy = DuplicatesStrategy.INCLUDE
}
""", written);

        Write(root, "gradle.properties", $$"""
org.gradle.jvmargs=-Xmx3G
org.gradle.daemon=false
mod_id={{modid}}
mod_version=1.0.0
mc_version={{p.McVersion}}
""", written);

        Write(root, "settings.gradle", """
pluginManagement {
    repositories {
        gradlePluginPortal()
        maven { url = 'https://maven.minecraftforge.net/' }
    }
}
plugins {
    id 'org.gradle.toolchains.foojay-resolver-convention' version '0.7.0'
}
""", written);

        string mainPath = $"src/main/java/{p.JavaPackagePath}/{mainClass}.java";
        Write(root, mainPath, $$"""
package {{p.Package}};

import net.minecraftforge.common.MinecraftForge;
import net.minecraftforge.eventbus.api.IEventBus;
import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.event.lifecycle.FMLCommonSetupEvent;
import net.minecraftforge.fml.javafmlmod.FMLJavaModLoadingContext;

// {{p.Description}}
@Mod({{mainClass}}.MODID)
public class {{mainClass}} {
    public static final String MODID = "{{modid}}";

    public {{mainClass}}() {
        IEventBus modBus = FMLJavaModLoadingContext.get().getModEventBus();
        modBus.addListener(this::setup);
        MinecraftForge.EVENT_BUS.register(this);
    }

    private void setup(final FMLCommonSetupEvent event) {
        // 初始化：注册物品 / 方块 / 网络包等
    }
}
""", written);

        Write(root, "src/main/resources/META-INF/mods.toml", $$"""
modLoader = "javafml"
loaderVersion = "[{{loaderMajor}},)"
license = "MIT"

[[mods]]
modId = "{{modid}}"
version = "1.0.0"
displayName = "{{p.Name}}"
authors = "{{p.Author}}"
description = '''
{{p.Description}}
'''

[[dependencies.{{modid}}]]
    modId = "forge"
    mandatory = true
    versionRange = "[{{loaderMajor}},)"
    ordering = "NONE"
    side = "BOTH"

[[dependencies.{{modid}}]]
    modId = "minecraft"
    mandatory = true
    versionRange = "[{{p.McVersion}}]"
    ordering = "NONE"
    side = "BOTH"
""", written);
        _ = pkgDir;
    }

    // ---------- NeoForge（NeoGradle userdev 模板） ----------
    private static void GenerateNeoForge(McProject p, string root, string pkgDir, string mainClass, string modid, McVersionInfo ver, List<string> written)
    {
        string neoVer = string.IsNullOrEmpty(ver.NeoForgeVersion) ? "21.1.77" : ver.NeoForgeVersion;
        string neoMajor = MajorVersion(ver);           // neoforge 依赖 versionRange，如 [21,)
        // 1.20.5 起 NeoForge 改用 META-INF/neoforge.mods.toml
        bool newToml = ver.IsCalendarVersion || ver.Version is "1.20.5" or "1.20.6" || ver.Version.StartsWith("1.21");
        string modsTomlRel = newToml ? "src/main/resources/META-INF/neoforge.mods.toml" : "src/main/resources/META-INF/mods.toml";

        Write(root, "build.gradle", $$"""
plugins {
    id 'net.neoforged.gradle.userdev' version '7.0.+'
}

version = '1.0.0'
group = '{{p.Package}}'
base { archivesName = '{{modid}}' }

java.toolchain.languageVersion = JavaLanguageVersion.of({{ver.JavaVersion}})

dependencies {
    implementation "net.neoforged:neoforge:{{neoVer}}"
{{(p.Dependencies.Any() ? string.Join("\n", p.Dependencies.Select(d => $"    {d.Scope} \"{d.Coordinate}\"")) : "")}}
}

runs {
    client {
        modSource sourceSets.main
    }
    server {
        modSource sourceSets.main
    }
}

tasks.withType(ProcessResources).configureEach {
    duplicatesStrategy = DuplicatesStrategy.INCLUDE
}
""", written);

        Write(root, "gradle.properties", $$"""
org.gradle.jvmargs=-Xmx3G
org.gradle.daemon=false
mod_id={{modid}}
mod_version=1.0.0
mc_version={{p.McVersion}}
""", written);

        Write(root, "settings.gradle", """
pluginManagement {
    repositories {
        gradlePluginPortal()
        maven { url = 'https://maven.neoforged.net/releases' }
    }
}
plugins {
    id 'org.gradle.toolchains.foojay-resolver-convention' version '0.7.0'
}
""", written);

        string mainPath = $"src/main/java/{p.JavaPackagePath}/{mainClass}.java";
        Write(root, mainPath, $$"""
package {{p.Package}};

import net.neoforged.bus.api.IEventBus;
import net.neoforged.fml.common.Mod;
import net.neoforged.fml.event.lifecycle.FMLCommonSetupEvent;

// {{p.Description}}
@Mod({{mainClass}}.MODID)
public class {{mainClass}} {
    public static final String MODID = "{{modid}}";

    public {{mainClass}}(IEventBus modEventBus) {
        modEventBus.addListener(this::setup);
    }

    private void setup(final FMLCommonSetupEvent event) {
        // 初始化：注册物品 / 方块 / 网络包等
    }
}
""", written);

        Write(root, modsTomlRel, $$"""
modLoader = "javafml"
loaderVersion = "[4,)"
license = "MIT"

[[mods]]
modId = "{{modid}}"
version = "1.0.0"
displayName = "{{p.Name}}"
authors = "{{p.Author}}"
description = '''
{{p.Description}}
'''

[[dependencies.{{modid}}]]
    modId = "neoforge"
    type = "required"
    versionRange = "[{{neoMajor}},)"
    ordering = "NONE"
    side = "BOTH"

[[dependencies.{{modid}}]]
    modId = "minecraft"
    type = "required"
    versionRange = "[{{p.McVersion}}]"
    ordering = "NONE"
    side = "BOTH"
""", written);
        _ = pkgDir;
    }

    // ---------- Forge 1.12.2 ----------
    private static void GenerateForge112(McProject p, string root, string pkgDir, string mainClass, string modid, List<string> written)
    {
        Write(root, "build.gradle", $$"""
buildscript {
    repositories {
        maven { url = 'https://maven.minecraftforge.net/' }
        mavenCentral()
    }
    dependencies { classpath 'net.minecraftforge.gradle:ForgeGradle:5.1.+' }
}
apply plugin: 'net.minecraftforge.gradle'

version = '1.0.0'
group = '{{p.Package}}'
archivesBaseName = '{{modid}}'

sourceCompatibility = targetCompatibility = '1.8'
compileJava { sourceCompatibility = targetCompatibility = '1.8' }

minecraft {
    mappings channel: 'stable_39', version: '1.12'
    runs { client { workingDirectory project.file('run') } }
}

dependencies {
    minecraft 'net.minecraftforge:forge:1.12.2-14.23.5.2859'
{{(p.Dependencies.Any() ? string.Join("\n", p.Dependencies.Select(d => $"    {d.Scope} \"{d.Coordinate}\"")) : "")}}
}
""", written);

        Write(root, "gradle.properties", "org.gradle.jvmargs=-Xmx3G\n", written);

        Write(root, "settings.gradle", """
pluginManagement {
    repositories {
        gradlePluginPortal()
        maven { url = 'https://maven.minecraftforge.net/' }
    }
}
""", written);

        Write(root, $"src/main/java/{p.JavaPackagePath}/{mainClass}.java", $$"""
package {{p.Package}};

import net.minecraftforge.fml.common.Mod;
import net.minecraftforge.fml.common.event.FMLInitializationEvent;
import net.minecraftforge.fml.common.event.FMLPreInitializationEvent;

@Mod(modid = {{mainClass}}.MODID, name = "{{p.Name}}", version = "1.0.0", acceptedMinecraftVersions = "[1.12.2]")
public class {{mainClass}} {
    public static final String MODID = "{{modid}}";

    @Mod.EventHandler
    public void preInit(FMLPreInitializationEvent event) {
        // 1.12.2：GameRegistry 注册物品 / 方块
    }

    @Mod.EventHandler
    public void init(FMLInitializationEvent event) {
        // 注册配方 / 事件
    }
}
""", written);

        Write(root, "src/main/resources/mcmod.info", $$"""
[{
  "modid": "{{modid}}",
  "name": "{{p.Name}}",
  "description": "{{p.Description}}",
  "version": "1.0.0",
  "mcversion": "1.12.2",
  "authorList": ["{{p.Author}}"]
}]
""", written);
        _ = pkgDir;
    }

    // ---------- Fabric ----------
    private static void GenerateFabric(McProject p, string root, string pkgDir, string mainClass, string modid, McVersionInfo ver, List<string> written)
    {
        string fabricLoader = string.IsNullOrEmpty(ver.FabricLoaderVersion) ? DefaultFabricLoader : ver.FabricLoaderVersion;
        string apiLine = string.IsNullOrEmpty(ver.FabricApiVersion)
            ? ""
            : $"\n    modImplementation \"net.fabricmc.fabric-api:fabric-api:{ver.FabricApiVersion}\"";
        // 2026 的 26.x 线 Fabric 工具链改用 Mojang 官方映射
        string mappingsLine = ver.IsCalendarVersion
            ? "mappings loom.officialMojangMappings()"
            : $"mappings \"net.fabricmc:yarn:{p.McVersion}+build.1:v2\"";

        Write(root, "build.gradle", $$"""
plugins {
    id 'fabric-loom' version '1.6-SNAPSHOT'
    id 'maven-publish'
}

version = '1.0.0'
group = '{{p.Package}}'

base { archivesName = '{{modid}}' }

repositories { mavenCentral() }

dependencies {
    minecraft "com.mojang:minecraft:{{p.McVersion}}"
    {{mappingsLine}}
    modImplementation "net.fabricmc:fabric-loader:{{fabricLoader}}"{{apiLine}}
{{(p.Dependencies.Any() ? string.Join("\n", p.Dependencies.Select(d => $"    {d.Scope} \"{d.Coordinate}\"")) : "")}}
}

java { withSourcesJar(); sourceCompatibility = JavaVersion.VERSION_{{ver.JavaVersion}}; targetCompatibility = JavaVersion.VERSION_{{ver.JavaVersion}} }
""", written);

        Write(root, "gradle.properties", "org.gradle.jvmargs=-Xmx3G\n", written);
        Write(root, "settings.gradle", "pluginManagement {\n    repositories {\n        maven { url = 'https://maven.fabricmc.net/' }\n        gradlePluginPortal()\n    }\n}\n", written);

        Write(root, $"src/main/java/{p.JavaPackagePath}/{mainClass}.java", $$"""
package {{p.Package}};

import net.fabricmc.api.ModInitializer;

// {{p.Description}}
public class {{mainClass}} implements ModInitializer {
    public static final String MODID = "{{modid}}";

    @Override
    public void onInitialize() {
        // Fabric：在此注册物品、方块、事件
    }
}
""", written);

        Write(root, "src/main/resources/fabric.mod.json", $$"""
{
  "schemaVersion": 1,
  "id": "{{modid}}",
  "version": "1.0.0",
  "name": "{{p.Name}}",
  "description": "{{p.Description}}",
  "authors": ["{{p.Author}}"],
  "license": "MIT",
  "environment": "*",
  "entrypoints": {
    "main": ["{{p.Package}}.{{mainClass}}"]
  },
  "depends": {
    "fabricloader": ">={{fabricLoader}}",
    "minecraft": "~{{p.McVersion}}",
    "java": ">={{ver.JavaVersion}}"{{(string.IsNullOrEmpty(ver.FabricApiVersion) ? "" : ",\n    \"fabric-api\": \"*\"")}}
  }
}
""", written);
        _ = pkgDir;
    }

    // ---------- Rift（1.12.2 轻量加载器，Fabric 风格）----------
    private static void GenerateFabricLike(McProject p, string root, string pkgDir, string mainClass, string modid, List<string> written)
    {
        Write(root, "build.gradle", $$"""
buildscript {
    repositories { mavenCentral(); maven { url 'https://www.dimdev.org/maven/' } }
    dependencies { classpath 'org.rivierarift:riftgradle:0.1.0' }
}
apply plugin: 'riftgradle'

group = '{{p.Package}}'
version = '1.0.0'

rift { version = '1.0.4-66' }

dependencies { minecraft '1.12.2' }
""", written);
        Write(root, $"src/main/java/{p.JavaPackagePath}/{mainClass}.java", $$"""
package {{p.Package}};

import org.dimdev.rift.listener.ModInitializer;

public class {{mainClass}} implements ModInitializer {
    public static final String MODID = "{{modid}}";

    @Override
    public void registerModData() {
        // Rift：注册内容
    }
}
""", written);
        Write(root, "gradle.properties", "org.gradle.jvmargs=-Xmx3G\n", written);
        _ = pkgDir;
    }
}
