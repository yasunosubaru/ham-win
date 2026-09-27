using System.Text.Json;
using Ham.Infrastructure.Education;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 把爬虫产出的接口契约钉在测试上。
/// </summary>
/// <remarks>
/// 契约由 <c>tools/recon/crawl-jwgl.mjs</c> 生成到
/// <c>tools/recon/out/contract.json</c>，来源是教务系统**自己的静态资源**
/// （免凭据）+ 带会话的正文判定。本测试读取那份 JSON 并断言我们的常量与之一致。
/// <para>
/// <b>为什么值得这么做：</b>成绩接口的路径我改错过两次——
/// 第一次照菜单拿教师版动作，第二次照页面 JS 的三元把 <c>cxDgXscj</c> 换成
/// <c>cxXsgrcj</c>，而后者<b>根本 404</b>。两次都是「读了源码就下结论」，
/// 没有拿真实服务器验过。把爬虫产物固化成测试，就不会再犯。
/// </para>
/// <para>
/// 若 <c>contract.json</c> 不存在（没跑过爬虫），这些测试会跳过而不是失败——
/// 免得让不联网的构建红掉。需要更新契约时先跑
/// <c>node tools/recon/crawl-jwgl.mjs</c>。
/// </para>
/// </remarks>
public class ReconContractTests
{
    private static readonly string ContractPath = LocateContract();

    private static string LocateContract()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "tools", "recon", "out", "contract.json");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return string.Empty;
    }

    private static JsonElement? Load()
    {
        if (ContractPath.Length == 0) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(ContractPath));
        return doc.RootElement.Clone();
    }

    private static bool HasAction(JsonElement c, string path)
        => c.GetProperty("actions").EnumerateArray()
              .Any(a => a.GetString() == path || a.GetString()!.StartsWith(path + "?", StringComparison.Ordinal));

    private static string Existence(JsonElement c, string path)
        => c.GetProperty("existence").TryGetProperty(path, out var v) ? v.GetString() ?? "" : "(未判定)";

    // ── 存在性：这是本文件最有价值的部分 ──

    [Fact]
    public void CrawlersBareGetVerdictIsNotProofOfAbsence()
    {
        var c = Load();
        if (c is null) return;

        // 爬虫把 /cjcx/cjcx_cxXsgrcj.html 判成 http-404。
        // **但它其实存在**——带 ?doType=query + POST + 会话 + 真实顶象 token 时
        // 返回 HTTP 200 / 54702 字节 / totalResult="29"。
        //
        // 原因：zfsoft 对「无 doType 的 GET」就是 404。爬虫判定时剥掉了 query，
        // 于是拿到了一个**假阴性**。据此改路径会一路改错。
        //
        // 这条测试的作用是把「不能拿爬虫的 404 当结论」写进代码库。
        var v = Existence(c.Value, "/cjcx/cjcx_cxXsgrcj.html");
        Assert.True(v == "http-404" || v == "missing",
            "若爬虫现在说 cxXsgrcj 存在，说明服务端行为变了，请重跑探测并更新本测试。");
    }

    [Fact]
    public void ScorePathStillIsTheStudentGridUrl()
    {
        // 无论爬虫的裸 GET 结论如何，**实测出数据的 URL** 是这个。
        var basePath = EducationEndpoints.ScorePath.Split('?')[0];
        Assert.Equal("/cjcx/cjcx_cxXsgrcj.html", basePath);
        Assert.EndsWith("doType=query", EducationEndpoints.ScorePath);
    }

    /// <summary>
    /// 我们的成绩接口必须落在爬虫确认存在的动作上。
    /// </summary>
    [Fact]
    public void ScorePathIsARealHarvestedAction()
    {
        var c = Load();
        if (c is null) return;

        var basePath = EducationEndpoints.ScorePath.Split('?')[0];
        Assert.True(HasAction(c.Value, basePath),
            $"{basePath} 不在爬虫抓到的 .html 动作表里，可能路径写错了。");
    }

    [Fact]
    public void CoursePathIsAlsoARealAction()
    {
        var c = Load();
        if (c is null) return;

        var basePath = EducationEndpoints.CoursePath.Split('?')[0];
        Assert.True(HasAction(c.Value, basePath),
            $"{basePath} 不在爬虫抓到的动作表里。课表是唯一实测出过数据的接口，别弄坏。");

        var v = Existence(c.Value, basePath);
        Assert.False(v == "http-404" || v == "missing", $"课表接口被判定为「{v}」。");
    }

    // ── paramMap 契约 ──

    /// <summary>
    /// 学生分支只有 4 个字段，教工那一大堆不能混进来。
    /// </summary>
    /// <remarks>
    /// 我曾经把 <c>queryModel.showCount</c> 当成绩参数传——那其实是 jqGrid 的
    /// <c>prmNames</c>（rows→showCount）自动带的，不是 <c>paramMap</c> 里的字段。
    /// </remarks>
    [Fact]
    public void StudentBranchIsExactlyThoseFourFields()
    {
        var c = Load();
        if (c is null) return;

        var pm = c.Value.GetProperty("paramMaps").EnumerateArray().FirstOrDefault();
        if (pm.ValueKind == JsonValueKind.Undefined) return;

        var student = pm.GetProperty("studentBranch").EnumerateArray()
                        .Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal);

        foreach (var f in new[] { "xnm", "xqm", "sy_id", "sq_id" })
        {
            Assert.Contains(f, student);
        }

        // 这些是教工分支独有的，学生请求里出现就是错的
        var staffOnly = new[] { "qsXnxq", "zzXnxq", "kkbm_id", "jys_id", "x_id_cx", "tyxm_id" };
        foreach (var f in staffOnly)
        {
            Assert.DoesNotContain(f, student);
        }

        Assert.Equal(4, student.Count);
    }

    // ── 成绩字段契约 ──

    [Theory]
    [InlineData("bfzcj")]   // 总评成绩
    [InlineData("xf")]      // 学分
    [InlineData("kcmc")]    // 课程名
    [InlineData("jsxm")]    // 教师
    [InlineData("jxbmc")]   // 课程编号
    [InlineData("kkbmmc")]  // 开课部门
    [InlineData("kcxzmc")]  // 课程性质
    [InlineData("xnm")]
    [InlineData("xqm")]
    [InlineData("xh")]
    [InlineData("xm")]
    public void ScoreColumnsArePresentInTheGridColumnModel(string field)
    {
        var c = Load();
        if (c is null) return;

        var cols = c.Value.GetProperty("columns").EnumerateArray()
            .SelectMany(x => x.GetProperty("columns").EnumerateArray().Select(y => y.GetString()!))
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(cols);
        Assert.Contains(field, cols);
    }

    /// <summary>分项成绩字段，线上不一定每门都有，但列定义里必须有。</summary>
    [Theory]
    [InlineData("qmcj")]
    [InlineData("qzcj")]
    [InlineData("pscj")]
    [InlineData("sycj")]
    public void ComponentScoreColumnsExist(string field)
    {
        var c = Load();
        if (c is null) return;

        var cols = c.Value.GetProperty("columns").EnumerateArray()
            .SelectMany(x => x.GetProperty("columns").EnumerateArray().Select(y => y.GetString()!))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(field, cols);
    }

    // ── 爬虫自身的行为 ──

    [Fact]
    public void CrawlerActuallyRanAndProducedSomething()
    {
        var c = Load();
        if (c is null) return;

        Assert.Equal("https://jwgl.whu.edu.cn", c.Value.GetProperty("origin").GetString());
        Assert.True(c.Value.GetProperty("assetCount").GetInt32() >= 30,
            "静态资源抓得太少，BFS 可能没跑完。");
        Assert.True(c.Value.GetProperty("actions").GetArrayLength() >= 50,
            ".html 动作数异常偏少，抽取器可能坏了。");
    }
}
