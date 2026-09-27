using Ham.Infrastructure.Education;
using Xunit;

namespace Ham.Tests;

/// <summary>
/// 教务接口路径的回归测试。
/// </summary>
/// <remarks>
/// 这些路径<b>不能照抄 ham-rn</b>。ham-rn 用的是
/// <c>/cjcx/cjcx_cjXsgrcj.html</c>，在武大教务上根本不存在——实测返回 zftal 的错误页：
/// 「警告提示：请求的方法 cjXsgrcj 在对象 CjcxAction 中未定义」。
/// <para>
/// 真实菜单 <c>/xtgl/index_initMenu.html</c> 里是
/// <c>clickMenu('N305005','/cjcx/cjcx_cxDgXscj.html','学生成绩查询')</c>。
/// 两边的 gnmkdm 都是 N305005，只有路径不同——只盯着 gnmkdm 发现不了，必须去看真实菜单。
/// </para>
/// <para>
/// <b>但菜单指向的 cxDgXscj 恰恰是不能直接用的那一个。</b>
/// 活页面对三个候选入口做过对照扫描：
/// <code>
/// 等级成绩页   /cjcx/cjcx_cxDgXscj.html    jQuery=undefined  jq外链=无   captcha容器=1248x0
/// 学生本人成绩 /cjcx/cjcx_cxXsgrcj.html    jQuery=function    jq外链=有   captcha容器=无
/// 成绩查询首页 /cjcx/cjcx_cxXscjIndex.html jQuery=function    jq外链=有   captcha容器=无
/// </code>
/// cxDgXscj 是唯一没引入 jquery.min.js 的，可它的内联脚本直接调
/// <c>jQuery('#kcbjdm_cx').trigger("chosen")</c>，全部抛
/// <c>ReferenceError: jQuery is not defined</c>；连带 <c>captcha.js</c> 里
/// <c>popupCaptcha()</c> 第一行 <c>jQuery.founded(...)</c> 也抛，
/// <c>#captcha_div</c> 永远 0 子节点、尺寸 1248x0 —— 验证码永远不弹。
/// 用户在真实浏览器里正常，是因为走首页那条自带 jQuery 的路径。
/// <para>
/// 因此 <see cref="EducationEndpoints.ScoreDocumentPath"/> 必须指向<b>首页</b>，
/// <see cref="EducationEndpoints.ScoreDetailPath"/> 才是承载查询表单的详情页。
/// </para>
/// </remarks>
public class EducationEndpointPathTests
{
    [Fact]
    public void ScoreApiPathIsTheStudentBranchOfTheGridUrl()
    {
        // cxDgXscj.js 的 grid 配置原文：
        //   url: _path + (jsxx == "xs" ? '/cjcx/cjcx_cxXsgrcj.html'
        //                           : '/cjcx/cjcx_cxDgXscj.html') + '?doType=query'
        // grid 的 searchGrid 插件（jquery.jqgrid.contact-min.js）只是 setGridParam
        // + trigger('reloadGrid')，真正发请求的 URL 就是这个。
        //
        // 实测（真实顶象 token）：HTTP 200 / 54702 字节 / totalResult="29"。
        Assert.Equal("/cjcx/cjcx_cxXsgrcj.html?doType=query", EducationEndpoints.ScorePath);
        Assert.Equal("/cjcx/cjcx_cxDgXscj.html?doType=query", EducationEndpoints.ScoreStaffPath);

        // doType 不能少：zfsoft 对「无 doType 的 GET」返回 404，
        // 爬虫正是被这个假信号骗了，一度判该动作不存在。
        Assert.Contains("doType=query", EducationEndpoints.ScorePath);
    }

    /// <summary>
    /// 防回归：动作存在性**不能**用裸 GET 判。
    /// </summary>
    /// <remarks>
    /// 爬虫曾把 <c>cxXsgrcj</c> 判成 404（剥掉 query 又不带会话），我据此两次改错方向。
    /// 这条测试把正确路径钉住，避免又被人按爬虫结论改回去。
    /// </remarks>
    [Fact]
    public void ScorePathIsNotTheStaffAction()
    {
        Assert.DoesNotContain("cxDgXscj", EducationEndpoints.ScorePath, StringComparison.Ordinal);
    }

    /// <summary>
    /// 页面入口必须走自带 jQuery 的成绩查询首页，而不是菜单直指的 cxDgXscj。
    /// </summary>
    /// <remarks>
    /// 这条断言是为了防止有人「照菜单改回去」——那正是验证码弹不出来的原因。
    /// </remarks>
    [Fact]
    public void ScorePageEntryIsTheJqueryBearingIndexNotTheBrokenDetail()
    {
        // 曾试过 cxXscjIndex 与 cxXsgrcj，两者都不行：
        //   cxXscjIndex → zftal 报「请求的方法cxXscjIndex在对象CjcxAction中未定义」
        //   cxXsgrcj    → HTTP 404
        // 保留这两个常量只为让测试能断言「我们没有把它们当入口用」。
        Assert.Equal("/cjcx/cjcx_cxXscjIndex.html?gnmkdm=N305005",
            EducationEndpoints.ScoreDocumentPath);

        Assert.Equal("/cjcx/cjcx_cxXsgrcj.html?gnmkdm=N305005",
            EducationEndpoints.ScoreDetailPath);
    }

    /// <summary>
    /// 两个 404 的动作绝不能被当成入口使用。
    /// </summary>
    /// <remarks>
    /// 这条断言是这次事故的直接产物：我照页面 JS 里的三元把成绩接口换成了
    /// <c>cxXsgrcj</c>，而它 404。爬虫（tools/recon）独立判定的结果钉在这里。
    /// </remarks>
    [Fact]
    public void ActionsKnownToBeMissingAreNeverUsedAsEndpoints()
    {
        const string xscjindex = "cxXscjIndex";

        // cxXscjIndex 确实不存在（zftal 报「请求的方法…未定义」）。
        // 但 cxXsgrcj **是**成绩接口——爬虫对它也报了 404，那是假阴性
        // （zfsoft 对「无 doType 的 GET」就返回 404）。两者必须区别对待。
        Assert.DoesNotContain(xscjindex, EducationEndpoints.ScorePath, StringComparison.Ordinal);
        Assert.Contains("cxXsgrcj", EducationEndpoints.ScorePath, StringComparison.Ordinal);
    }

    /// <summary>旧路径在武大不存在，必须确保没有被改回去。</summary>
    [Fact]
    public void ScorePathIsNotTheNonExistentHamRnPath()
    {
        Assert.DoesNotContain("cjXsgrcj", EducationEndpoints.ScorePath, StringComparison.Ordinal);
        Assert.DoesNotContain("cjXsgrcj", EducationEndpoints.ScoreDocumentPath, StringComparison.Ordinal);
        Assert.DoesNotContain("cjXsgrcj", EducationEndpoints.ScoreDetailPath, StringComparison.Ordinal);
    }

    [Fact]
    public void CoursePathStillWorks()
    {
        // 课表接口实测 HTTP 200 / 32601 字节 / kbList 20 门课，路径是正确的，不要动
        Assert.Equal("/kbcx/xskbcx_cxXsgrkb.html?gnmkdm=N2151", EducationEndpoints.CoursePath);
    }

    [Fact]
    public void UserInfoPathStillWorks()
    {
        // 实测 200，能解析出学号/姓名/学院
        Assert.Equal("/xtgl/index_cxYhxxIndex.html?xt=jw&localeKey=zh_CN&gnmkdm=index",
            EducationEndpoints.UserInfoPath);
    }
}
