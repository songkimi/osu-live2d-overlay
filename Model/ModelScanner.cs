using System.IO;
using System.Text.Json;

namespace OsuLive2dOverlay;

public enum ModelResourceKind
{
    /// <summary>表情（*.exp3.json）</summary>
    Expression,

    /// <summary>动作（*.motion3.json；已注册的是一个"组"，可能含多个文件）</summary>
    Motion
}

/// <summary>模型里一个可引用的资源</summary>
public sealed record ModelResource(
    string Id,                          // 标识：2.exp3 / Idle / 话筒.motion3
    ModelResourceKind Kind,
    bool Registered,                    // 是否由入口文件注册过
    IReadOnlyList<string> Files);       // 相对模型目录的路径（动作组可能多个）

/// <summary>一次扫描的结果,包括表情，动作类和扫描中遇到的问题</summary>
public sealed record ModelScanResult(
    IReadOnlyList<ModelResource> Expressions,
    IReadOnlyList<ModelResource> Motions,
    IReadOnlyList<string> Problems);    // 扫描中遇到的麻烦（给界面显示）
/// <summary>
/// 存入表情组，动作组组
/// </summary>
public class Model3FileReferences
{
    public List<ExpRefItem>? Expressions { get; set; }
    public Dictionary<string, List<MotionRefItem>>? Motions { get; set; }
}
public class ExpRefItem
{
    public string File { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
public class MotionRefItem
{
    public string File { get; set; } = string.Empty;
}
/// <summary>
/// Json解析完成的Modle3
/// </summary>
public class Model3Root
{
    public Model3FileReferences FileReferences { get; set; } = new();
}

public static class ModelScanner
{
    /// <summary>
    /// 在模型目录里**找出**入口文件（`*.model3.json`）。
    ///
    /// 【为什么需要它】「形象」页上"模型入口"是**只读**的 —— 用户自己的原话：
    /// "模型入口该由扫描器决定，界面上应该是 readonly 展示，让用户手打很危险"。
    /// 所以入口不能靠用户输，只能由扫描器找出来。而 <see cref="Scan"/> 是**先要入口**
    /// 才肯扫的，两者之间就缺了这一步。
    ///
    /// 返回的是**相对模型目录的路径**（不是完整路径）—— 因为 `Scan` 收的是
    /// `Path.Combine(模型目录, 入口)`，相对路径正好能用，顺带也说明了它藏在哪个子目录里。
    ///
    /// 顶层优先、再往下找：大多数模型把 model3.json 放在模型目录的根上，
    /// 但也见过塞在子目录里的。绝不抛异常（目录不存在 → 空列表）。
    /// </summary>
    public static IReadOnlyList<string> FindEntries(string modelDirectory)
    {
        var found = new List<string>();

        try
        {
            if (!Directory.Exists(modelDirectory)) return found;

            var root = Path.GetFullPath(modelDirectory);

            foreach (var file in Directory.EnumerateFiles(root, "*.model3.json", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file);
                if (relative.StartsWith("..", StringComparison.Ordinal)) continue;   // 不该发生，防一手

                found.Add(relative);
            }
        }
        catch
        {
            // 读不动就当没找到 —— 界面会显示"没找到模型入口"，比抛异常好
        }

        // 顶层的排前面（"绒绒.model3.json" 只有一个分隔符都没有），再按名字
        found.Sort((a, b) =>
        {
            var depthA = a.Count(c => c is '\\' or '/');
            var depthB = b.Count(c => c is '\\' or '/');

            return depthA != depthB
                ? depthA.CompareTo(depthB)
                : string.Compare(a, b, StringComparison.CurrentCulture);
        });

        return found;
    }

    /// <summary>
    /// 扫描一个模型。
    /// modelDirectory = 模型目录；entryFileName = 入口文件名（如 "绒绒.model3.json"）
    /// 任何异常都不往外抛，一律记进 Problems 并返回能拿到的那部分结果。
    /// </summary>
    public static ModelScanResult Scan(string modelDirectory, string entryFileName)
    {
        string entryFilePath = Path.Combine(modelDirectory, entryFileName);
        bool firstCheck = true;
        var problems = new List<string>();
        if (!Directory.Exists(modelDirectory))
        {
            problems.Add($"模型目录不存在{modelDirectory}");
            firstCheck = false;
        }
        if (!File.Exists(entryFilePath))
        {
            problems.Add($"没有能执行的model3.json入口文件");
        }
        //           表情：文件路径 → 名字
        //           动作：文件路径 → 组名（注意一个组里可能有多个文件）
        //          键要**规范化**：分隔符统一成 '/'、统一大小写 —— 否则匹配不上
        var expRegister = new Dictionary<string, string>();
        var motionRegister = new Dictionary<string, string>();
        try
        {
            string jsonText = File.ReadAllText(entryFilePath);
            var model3 = JsonSerializer.Deserialize<Model3Root>(jsonText) ?? throw new Exception("解析model3.json失败");
            if (model3.FileReferences.Expressions != null)
            {
                foreach (var expItem in model3.FileReferences.Expressions)
                {
                    string normPath = NormalizePath(expItem.File);
                    expRegister[normPath] = expItem.Name;
                }
            }
            if (model3.FileReferences.Motions != null)
            {
                foreach (var motionGroupKvp in model3.FileReferences.Motions)
                {
                    string groupName = motionGroupKvp.Key;
                    var fileList = motionGroupKvp.Value;
                    foreach (var file in fileList)
                    {
                        string normPath = NormalizePath(file.File);
                        motionRegister[normPath] = groupName;
                    }
                }
            }
        }
        catch(Exception ex)
        {
            problems.Add($"解析model3.json 失败：{ex.Message}");
        }
        //
        //          注意 .vtube.json 不是 exp3，别误收
        var diskExpPath = new List<(string,string)>();
        var diskMotionPath = new List<(string, string)>();
        if(firstCheck)  ScanDirectory(modelDirectory,modelDirectory ,diskExpPath, diskMotionPath);
        //          按「已注册优先，其次文件名」；
        //          动作组的标识是**组名**，同一个组只产出一条（Files 里放全部文件）
        var motionResources = GetMotionGroup(diskMotionPath, motionRegister);
        var expResourcs = GetExpResoures(diskExpPath, expRegister);
        if (motionResources.Item2 != null) problems.AddRange(motionResources.Item2);
        if (expResourcs.Item2 != null) problems.AddRange(expResourcs.Item2);
        motionResources.Item1 = motionResources.Item1.OrderByDescending(r => r.Registered).ThenBy(r => r.Id).ToList();
        expResourcs.Item1 = expResourcs.Item1.OrderBy(r => r.Id).ToList();
        return new ModelScanResult(
            Motions:motionResources.Item1,
            Expressions:expResourcs.Item1,
            Problems:problems);
    }
    /// <summary>
    /// 路径统一，分隔符换成‘/’,消除Windows反斜杠，大小写等问题
    /// </summary>
    /// <param name="rawPath"></param>
    /// <returns></returns>
    private static string NormalizePath(string rawPath)
    {
        return rawPath.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();
    }

    /// <summary>
    /// 递归扫描路径，收集所有exp3，motion3文件，返回相对模型目录的规范化目录
    /// </summary>
    /// <param name="modelRoot">永远不变的根目录</param>
    /// <param name="currentDir">持续更新的子目录</param>
    /// <param name="expFiles">所有的exp3文件路径(修正路径，原始路径)</param>
    /// <param name="motionFile">所有的motion3文件路径（修正路径，原始路径）</param>
    private static void ScanDirectory(string modelRoot,string currentDir,List<(string,string)> expFiles, List<(string,string)> motionFile)
    {
        foreach (var file in Directory.GetFiles(currentDir))
        {
            string fileName = Path.GetFileName(file);
            if (fileName.Equals("vtube.json", StringComparison.OrdinalIgnoreCase)) continue;
            string relativePath = Path.GetRelativePath(modelRoot,file);//获取相对路径
            string normRelPath = NormalizePath(relativePath);
            if (fileName.EndsWith(".exp3.json", StringComparison.OrdinalIgnoreCase)) expFiles.Add((normRelPath,relativePath));
            else if (fileName.EndsWith(".motion3.json", StringComparison.OrdinalIgnoreCase)) motionFile.Add((normRelPath, relativePath));
        }
        foreach (var subDir in Directory.GetDirectories(currentDir))
        {
            ScanDirectory(modelRoot,subDir, expFiles, motionFile);
        }
    }
    /// <summary>
    /// 聚合动作组方法
    /// </summary>
    /// <param name="diskMotionPath">得到的全部符合格式的文件路径</param>
    /// <param name="motionRegister">全部得到的已被注册的(路径,组类)字典</param>
    /// <returns></returns>
    private static (List<ModelResource>,List<string>?) GetMotionGroup(List<(string,string)> diskMotionPath, Dictionary<string, string> motionRegister)
    {
        Dictionary<string, List<string>> motionGroupAgg = new();
        List<string> problems = new();
        foreach (var diskMotionNormPath in diskMotionPath)
        {
            bool isRegistered = motionRegister.TryGetValue(diskMotionNormPath.Item1, out string? groupName);
            if (!isRegistered)
            {
                groupName = Path.GetFileNameWithoutExtension(diskMotionNormPath.Item2);//用文件名兜底
            }
            //重名冲突检查
            if (motionGroupAgg.ContainsKey(groupName!) && !isRegistered)
            {
                //已经存在这个组名
                //1.这个文件是注册文件，现存组是兜底来的
                //2.相反
                //都需要保留最先扫描到的一组
                problems.Add($"警告，动作组ID {groupName} 发生ID冲突，已自动保留首个被扫描的资源，跳过文件 {diskMotionNormPath} ");
                continue;
            }
            if (!motionGroupAgg.ContainsKey(groupName!))
            {
                motionGroupAgg[groupName!] = new List<string>();
            }
            motionGroupAgg[groupName!].Add(diskMotionNormPath.Item1);
        }
        
            var motionResources = new List<ModelResource>();
        foreach (var kvp in motionGroupAgg)
        {
            string groupId = kvp.Key;
            List<string> files = kvp.Value;
            bool groupIsregistered = motionRegister.ContainsKey(files[0]);
            motionResources.Add(new ModelResource(Id: groupId, Kind: ModelResourceKind.Motion, Registered: groupIsregistered, Files: files.AsReadOnly()));
        }
        return (motionResources,problems);
    }
    /// <summary>
    /// 整理表情集合
    /// </summary>
    /// <param name="diskExpPath">符合格式的文件路径</param>
    /// <param name="expRegister">已被注册的表情</param>
    /// <returns></returns>
    private static (List<ModelResource>,List<string>?) GetExpResoures(List<(string,string)> diskExpPath, Dictionary<string, string> expRegister)
    {
        List<ModelResource> expResources = new();
        List<string> problems = new();
        HashSet<string> usedIds = new();
        foreach (var diskExpNormPath in diskExpPath)
        {
            bool isReGistered = expRegister.TryGetValue(diskExpNormPath.Item1, out string? dispalyName);
            if (!isReGistered)
            {
                dispalyName = Path.GetFileNameWithoutExtension(diskExpNormPath.Item2);
            }
            if (string.IsNullOrEmpty(dispalyName))
            {
                problems.Add($"警告； 文件{diskExpNormPath.Item2}无法生成有效ID，自动跳过");
                continue;
            }
            if (usedIds.Contains(dispalyName) && !isReGistered)
            {
                problems.Add($"警告：表情ID {dispalyName} 发生ID冲突，已自动保留首个被扫描的资源，跳过文件 {diskExpNormPath.Item2} ");
                continue;
            }
            usedIds.Add(dispalyName);
            expResources.Add(new ModelResource(Id:dispalyName, Kind:ModelResourceKind.Expression,Registered:isReGistered, Files:new[]{diskExpNormPath.Item2}.AsReadOnly()));
        }
        return (expResources,problems);
    }
}
