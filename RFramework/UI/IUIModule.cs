using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// UI 模块接口。负责窗口异步加载、生命周期、层级与全屏遮挡管理。
    /// </summary>
    public interface IUIModule
    {
        /// <summary>
        /// 获取当前打开的 UI 数量。
        /// </summary>
        int UIFormCount { get; }

        /// <summary>
        /// 设置依赖模块引用（由 UIComponent 在 Awake 中注入）。
        /// </summary>
        void SetDependencies(IResourceModule resourceModule, IEventModule eventModule);

        /// <summary>
        /// 设置 UI 辅助器。
        /// </summary>
        void SetHelper(IUIHelper helper);

        /// <summary>
        /// 异步打开 UI。同一 UI 不可重复打开（需先 CloseUIForm）。
        /// </summary>
        /// <param name="assetName">UI 资源路径。</param>
        /// <param name="windowLayer">窗口层级（数值越大越靠前）。</param>
        /// <param name="fullScreen">是否全屏（覆盖时自动隐藏下层 UI）。</param>
        /// <param name="priority">加载优先级。</param>
        /// <param name="userData">用户自定义数据。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>打开的 UI 表单。</returns>
        Task<IUIForm> OpenUIFormAsync(string assetName, int windowLayer = 0,
            bool fullScreen = false, uint priority = 0, object userData = null,
            CancellationToken ct = default);

        /// <summary>
        /// 登记由外部创建并持有的 UI 表单，例如场景中预先放置的 UI。
        /// 外部 UI 参与窗口栈、全屏遮挡和模块更新，但关闭时不会释放实例或卸载资源。
        /// </summary>
        /// <param name="formName">UI 表单唯一名称。</param>
        /// <param name="uiForm">已创建的 UI 表单。</param>
        /// <param name="userData">用户自定义数据。</param>
        void RegisterUIForm(string formName, IUIForm uiForm, object userData = null);

        /// <summary>
        /// 注销由外部创建并持有的 UI 表单，不释放其实例或资源。
        /// </summary>
        /// <param name="formName">UI 表单唯一名称。</param>
        /// <param name="userData">用户自定义数据。</param>
        void UnregisterUIForm(string formName, object userData = null);

        /// <summary>
        /// 关闭 UI。
        /// </summary>
        /// <param name="assetName">UI 资源路径。</param>
        /// <param name="userData">用户自定义数据。</param>
        void CloseUIForm(string assetName, object userData = null);

        /// <summary>
        /// 关闭所有已打开的 UI。
        /// </summary>
        /// <param name="userData">用户自定义数据。</param>
        void CloseAllUIForms(object userData = null);

        /// <summary>
        /// 判断 UI 是否已打开。
        /// </summary>
        bool HasUIForm(string assetName);

        /// <summary>
        /// 获取 UI 表单。
        /// </summary>
        IUIForm GetUIForm(string assetName);

        /// <summary>
        /// 获取当前窗口栈顶的 UI；没有已打开窗口时返回 null。
        /// </summary>
        IUIForm GetTopUIForm();

        /// <summary>
        /// 关闭当前窗口栈顶的 UI，并恢复其下方窗口。
        /// </summary>
        /// <param name="userData">用户自定义数据。</param>
        /// <returns>成功关闭返回 true；没有已打开窗口时返回 false。</returns>
        bool CloseTopUIForm(object userData = null);

        /// <summary>
        /// 获取所有已打开的 UI。
        /// </summary>
        IUIForm[] GetAllUIForms();

        /// <summary>
        /// 获取所有正在加载中的 UI 资源路径。
        /// </summary>
        string[] GetAllLoadingUIFormAssetNames();

        /// <summary>
        /// 判断 UI 是否正在加载中。
        /// </summary>
        bool IsLoadingUIForm(string assetName);
    }
}
