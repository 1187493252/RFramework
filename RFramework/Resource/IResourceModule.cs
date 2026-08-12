using System;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 资源模块接口。
    /// 对外不暴露任何引擎特定类型，所有异步操作通过 Task 驱动。
    /// </summary>
    public interface IResourceModule
    {
        /// <summary>
        /// 设置资源辅助器（必须在 InitializeAsync 之前调用）。
        /// 辅助器封装了所有引擎特定资源操作，由 Runtime 层实现。
        /// </summary>
        void SetHelper(IResourceHelper helper);

        /// <summary>
        /// 设置资源运行模式（必须在 InitializeAsync 之前调用）
        /// </summary>
        void SetPlayMode(ResourcePlayMode playMode);

        /// <summary>
        /// 设置远程资源服务器地址（Host 模式必须）
        /// </summary>
        /// <param name="defaultHostServer">默认 CDN 地址</param>
        /// <param name="fallbackHostServer">备用 CDN 地址</param>
        void SetRemoteServiceUrl(string defaultHostServer, string fallbackHostServer);

        /// <summary>
        /// 设置资源包裹名称（必须在 InitializeAsync 之前调用，默认 "DefaultPackage"）
        /// </summary>
        void SetPackageName(string packageName);

        /// <summary>
        /// 初始化资源系统。调用前必须先设置 PlayMode 和 RemoteServiceUrl。
        /// 异步初始化资源系统，包括资源包裹创建与文件系统挂载。
        /// </summary>
        Task InitializeAsync();

        /// <summary>
        /// 异步加载资源。同一资源并发请求自动去重（第二个请求等待第一个完成）。
        /// 返回的资源通过引用计数管理——每调用一次 LoadAssetAsync 计数 +1。
        /// </summary>
        /// <typeparam name="T">资源类型</typeparam>
        /// <param name="location">资源路径（如 "Assets/Prefabs/Player.prefab"）</param>
        /// <param name="priority">加载优先级（越大越优先）</param>
        /// <param name="ct">取消令牌</param>
        /// <param name="onProgress">加载进度回调（0~1），可为 null。</param>
        Task<T> LoadAssetAsync<T>(string location, uint priority = 0, CancellationToken ct = default,
            IProgress<float> onProgress = null)
            where T : class;

        /// <summary>
        /// 同步加载资源（由 Runtime 辅助器内部阻塞等待异步完成）。
        /// 仅在确实需要同步加载时使用，不建议在热更层调用。
        /// </summary>
        T LoadAssetSync<T>(string location) where T : class;

        /// <summary>
        /// 异步加载场景
        /// </summary>
        /// <param name="location">场景资源路径</param>
        /// <param name="sceneMode">场景加载模式：0=Single 替换当前场景，1=Additive 叠加到当前场景（与 UnityEngine.SceneManagement.LoadSceneMode 值一致）</param>
        /// <param name="activateOnLoad">是否加载完成后立即激活。当前内置 Helper 仅支持 true；传 false 会抛出 NotSupportedException，避免无激活入口的任务永久等待。</param>
        /// <param name="priority">加载优先级</param>
        /// <param name="onProgress">进度回调（0~1），可为 null</param>
        Task LoadSceneAsync(string location, int sceneMode = 0,
            bool activateOnLoad = true, uint priority = 0, IProgress<float> onProgress = null);

        /// <summary>
        /// 异步卸载场景
        /// </summary>
        Task UnloadSceneAsync(string location);

        /// <summary>
        /// 卸载资源，引用计数 -1。当计数归零且超过过期时间后自动释放。
        /// </summary>
        /// <param name="asset">LoadAssetAsync / LoadAssetSync 返回的资源对象</param>
        void UnloadAsset(object asset);

        /// <summary>
        /// 按加载路径和泛型类型精确归还一次资源引用。
        /// 当同一底层对象以多个路径或类型被加载时，应使用此重载，避免
        /// <see cref="UnloadAsset(object)"/> 的对象引用查找产生歧义。
        /// </summary>
        void UnloadAsset<T>(string location) where T : class;

        /// <summary>
        /// 立即释放所有引用计数为 0 的资源。
        /// </summary>
        void UnloadUnusedAssets();

        /// <summary>
        /// 检查资源是否存在且可用
        /// </summary>
        bool HasAsset(string location);

        /// <summary>
        /// 获取指定资源的下载大小（字节），用于判断是否需要下载
        /// </summary>
        long GetDownloadSize(string location);

        /// <summary>
        /// 获取当前已加载资源的数量
        /// </summary>
        int LoadedAssetCount { get; }

        /// <summary>
        /// 获取待加载资源数量（正在异步加载中的资源数）
        /// </summary>
        int LoadingAssetCount { get; }
    }
}
