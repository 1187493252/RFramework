using System;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 资源辅助器接口。
    /// 封装所有引擎特定资源操作，由 Runtime 层实现，ResourceModule 通过此接口与引擎底层解耦。
    /// </summary>
    public interface IResourceHelper
    {
        /// <summary>
        /// 初始化资源系统。
        /// 内部完成资源系统的初始化、资源包裹创建与文件系统挂载。
        /// </summary>
        /// <param name="packageName">资源包裹名称</param>
        /// <param name="playMode">资源运行模式</param>
        /// <param name="defaultHostServer">默认 CDN 地址（Host 模式使用）</param>
        /// <param name="fallbackHostServer">备用 CDN 地址</param>
        Task InitializeAsync(string packageName, ResourcePlayMode playMode,
            string defaultHostServer, string fallbackHostServer);

        /// <summary>
        /// 销毁资源系统。
        /// 内部释放所有资源句柄、移除资源包裹并销毁资源系统。
        /// </summary>
        void Destroy();

        /// <summary>
        /// 异步加载资源并返回原始对象。
        /// 辅助器内部持有底层资源句柄，后续通过 ReleaseAsset 释放。
        /// </summary>
        /// <param name="location">资源路径（如 "Assets/Prefabs/Player.prefab"）</param>
        /// <param name="assetType">资源类型</param>
        /// <param name="priority">加载优先级（越大越优先）</param>
        /// <param name="ct">取消令牌，调用方已取消时应尽早中止底层加载</param>
        /// <param name="onProgress">加载进度回调（0~1），可为 null。</param>
        /// <returns>加载的资源对象</returns>
        Task<object> LoadAssetAsync(string location, Type assetType, uint priority,
            CancellationToken ct = default, IProgress<float> onProgress = null);

        /// <summary>
        /// 同步加载资源并返回原始对象。
        /// </summary>
        /// <param name="location">资源路径</param>
        /// <param name="assetType">资源类型</param>
        /// <returns>加载的资源对象</returns>
        object LoadAssetSync(string location, Type assetType);

        /// <summary>
        /// 释放指定位置的资源句柄。
        /// 调用后该位置的资源不再被底层持有，但已返回给调用方的对象引用仍然有效。
        /// </summary>
        /// <param name="location">资源路径</param>
        /// <param name="assetType">加载该资源时使用的资源类型</param>
        void ReleaseAsset(string location, Type assetType);

        /// <summary>
        /// 异步加载场景。
        /// 内部完成场景资源的创建和等待，辅助器持有场景句柄供后续卸载。
        /// </summary>
        /// <param name="location">场景资源路径</param>
        /// <param name="sceneMode">场景加载模式：0=Single 替换当前场景，1=Additive 叠加到当前场景（与 UnityEngine.SceneManagement.LoadSceneMode 值一致）</param>
        /// <param name="activateOnLoad">是否加载完成后立即激活</param>
        /// <param name="priority">加载优先级</param>
        /// <param name="onProgress">进度回调（0~1），可为 null</param>
        Task LoadSceneAsync(string location, int sceneMode, bool activateOnLoad, uint priority,
            IProgress<float> onProgress = null);

        /// <summary>
        /// 异步卸载场景。
        /// 释放内部持有的场景句柄并等待卸载完成。
        /// </summary>
        /// <param name="location">场景资源路径</param>
        Task UnloadSceneAsync(string location);

        /// <summary>
        /// 检查指定位置的资源是否存在于资源包裹中。
        /// </summary>
        /// <param name="location">资源路径</param>
        bool IsLocationValid(string location);

        /// <summary>
        /// 获取指定资源的下载大小（字节）。
        /// Host 模式下用于判断是否需要下载。
        /// </summary>
        /// <param name="location">资源路径</param>
        long GetDownloadSize(string location);
    }
}
