using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// 资源模块核心实现。
    /// 负责引用计数管理、并发加载去重、延迟释放调度。
    /// 所有引擎特定资源操作委托给 IResourceHelper，保持 Library 层纯 C#。
    /// </summary>
    internal sealed class ResourceModule : RFrameworkModule, IResourceModule
    {
        /// <summary>
        /// 获取框架模块优先级。
        /// 资源是配置、场景、实体、UI、音频和本地化的共同依赖。
        /// 高优先级使其先于消费者更新，并在关闭时最后释放。
        /// </summary>
        internal override int Order
        {
            get { return 50; }
        }

        // ==================== 依赖注入 ====================

        /// <summary>资源辅助器，封装所有引擎特定资源操作</summary>
        private IResourceHelper helper;

        /// <summary>事件模块引用，用于分发加载失败事件。惰性获取，可能为 null（事件模块未就绪时）。</summary>
        private IEventModule eventModule;

        // ==================== 配置 ====================

        /// <summary>资源运行模式</summary>
        private ResourcePlayMode playMode = ResourcePlayMode.EditorSimulate;

        /// <summary>默认 CDN 地址（Host 模式使用）</summary>
        private string defaultHostServer;

        /// <summary>备用 CDN 地址</summary>
        private string fallbackHostServer;

        /// <summary>资源包裹名称</summary>
        private string packageName = "DefaultPackage";

        /// <summary>是否已完成初始化</summary>
        private bool isInitialized;

        /// <summary>当前初始化任务。并发调用共享同一初始化流程。</summary>
        private Task initializationTask;

        /// <summary>是否已进入关闭流程。置位后新加载请求直接失败，在飞加载续体不再写回缓存。</summary>
        private bool isShutdown;

        // ==================== 资源缓存（引用计数） ====================

        /// <summary>
        /// 资源缓存键：规范化路径 + 资源类型。
        /// 同一路径以不同泛型类型加载时视为不同资源（避免类型不匹配却返回 null 且引用计数仍 +1）；
        /// 规范化路径使 "A" 与 "A.prefab" 映射到同一条缓存记录，避免重复缓存与重复释放。
        /// </summary>
        private readonly struct AssetCacheKey : IEquatable<AssetCacheKey>
        {
            /// <summary>规范化后的资源路径（已去扩展名）。</summary>
            public readonly string Location;

            /// <summary>资源类型。</summary>
            public readonly Type AssetType;

            public AssetCacheKey(string location, Type assetType)
            {
                Location = location;
                AssetType = assetType;
            }

            public bool Equals(AssetCacheKey other)
            {
                return string.Equals(Location, other.Location) && AssetType == other.AssetType;
            }

            public override bool Equals(object obj)
            {
                return obj is AssetCacheKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return ((Location != null ? Location.GetHashCode() : 0) * 397) ^
                           (AssetType != null ? AssetType.GetHashCode() : 0);
                }
            }
        }

        /// <summary>已加载资源缓存：AssetCacheKey → CachedAsset</summary>
        private readonly Dictionary<AssetCacheKey, CachedAsset> loadedAssets =
            new Dictionary<AssetCacheKey, CachedAsset>();

        /// <summary>正在加载中的资源并发去重：AssetCacheKey → TCS 列表</summary>
        private readonly Dictionary<AssetCacheKey, List<TaskCompletionSource<CachedAsset>>> pendingLoads =
            new Dictionary<AssetCacheKey, List<TaskCompletionSource<CachedAsset>>>();

        /// <summary>保护 loadedAssets / pendingLoads 跨线程访问的锁（底层加载续体可能在线程池线程上执行）。</summary>
        private readonly object loadLock = new object();

        /// <summary>模块级取消令牌源。关闭时触发，使所有在飞底层加载（含等待中的 Task）随模块一起终止。</summary>
        private readonly CancellationTokenSource moduleCts = new CancellationTokenSource();

        /// <summary>已加载的场景 location 集合</summary>
        private readonly HashSet<string> loadedScenes = new HashSet<string>();

        /// <summary>待回收资源队列（UnloadAsset 后引用计数为 0 的资源）</summary>
        private readonly Queue<CachedAsset> releaseQueue = new Queue<CachedAsset>();

        // ==================== 内部类型 ====================

        /// <summary>
        /// 已加载资源的内部缓存对象。追踪引用计数，但不持有底层句柄（句柄由 IResourceHelper 管理）。
        /// </summary>
        private sealed class CachedAsset
        {
            /// <summary>资源路径</summary>
            public string Location;

            /// <summary>加载的资源对象</summary>
            public object Asset;

            /// <summary>资源类型</summary>
            public Type AssetType;

            /// <summary>引用计数</summary>
            public int RefCount;

            /// <summary>是否可以被释放（引用计数为 0）</summary>
            public bool CanRelease
            {
                get { return RefCount <= 0; }
            }
        }

        // ==================== 配置方法 ====================

        /// <summary>
        /// 设置资源辅助器（必须在 InitializeAsync 之前调用）。
        /// </summary>
        /// <param name="helper">资源辅助器实例，由 Runtime 层创建并注入。</param>
        public void SetHelper(IResourceHelper helper)
        {
            if (isInitialized)
            {
                throw new InvalidOperationException("ResourceModule: Cannot change helper after initialization.");
            }

            this.helper = helper ?? throw new RFrameworkException("Resource helper is invalid.");
        }

        /// <summary>
        /// 设置资源运行模式（必须在 InitializeAsync 之前调用）。
        /// </summary>
        /// <param name="playMode">资源运行模式。</param>
        public void SetPlayMode(ResourcePlayMode playMode)
        {
            if (isInitialized)
            {
                throw new InvalidOperationException("ResourceModule: Cannot change play mode after initialization.");
            }

            this.playMode = playMode;
        }

        /// <summary>
        /// 设置远程资源服务器地址（Host 模式必须）。
        /// </summary>
        /// <param name="defaultHostServer">默认 CDN 地址。</param>
        /// <param name="fallbackHostServer">备用 CDN 地址。</param>
        public void SetRemoteServiceUrl(string defaultHostServer, string fallbackHostServer)
        {
            if (isInitialized)
            {
                throw new InvalidOperationException(
                    "ResourceModule: Cannot change remote service URL after initialization.");
            }

            this.defaultHostServer = defaultHostServer;
            this.fallbackHostServer = fallbackHostServer;
        }

        /// <summary>
        /// 设置资源包裹名称（必须在 InitializeAsync 之前调用，默认 "DefaultPackage"）。
        /// </summary>
        /// <param name="packageName">资源包裹名称。</param>
        public void SetPackageName(string packageName)
        {
            if (isInitialized)
            {
                throw new InvalidOperationException(
                    "ResourceModule: Cannot change package name after initialization.");
            }

            this.packageName = packageName ?? "DefaultPackage";
        }

        // ==================== 初始化 ====================

        /// <summary>
        /// 初始化资源系统。调用前必须先设置 PlayMode、RemoteServiceUrl 和 Helper。
        /// 异步等待资源系统初始化、资源包裹创建及文件系统挂载完成。
        /// </summary>
        public Task InitializeAsync()
        {
            if (isInitialized)
            {
                return Task.CompletedTask;
            }

            if (initializationTask != null)
            {
                return initializationTask;
            }

            initializationTask = InitializeInternalAsync();
            return initializationTask;
        }

        private async Task InitializeInternalAsync()
        {
            try
            {
                if (isShutdown)
                {
                    throw new ObjectDisposedException(nameof(ResourceModule));
                }

                EnsureHelper();
                await helper.InitializeAsync(packageName, playMode, defaultHostServer, fallbackHostServer);

                if (isShutdown)
                {
                    throw new ObjectDisposedException(nameof(ResourceModule));
                }

                isInitialized = true;
            }
            finally
            {
                if (!isInitialized)
                {
                    initializationTask = null;
                }
            }
        }

        // ==================== 资源加载 ====================

        /// <summary>
        /// 异步加载资源。同一资源并发请求自动去重（第二个请求等待第一个完成）。
        /// 返回的资源通过引用计数管理——每调用一次 LoadAssetAsync 计数 +1。
        /// </summary>
        /// <typeparam name="T">资源类型。</typeparam>
        /// <param name="location">资源路径。</param>
        /// <param name="priority">加载优先级（越大越优先）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <param name="onProgress">加载进度回调（0~1），可为 null。</param>
        /// <returns>加载的资源对象。</returns>
        public async Task<T> LoadAssetAsync<T>(string location, uint priority = 0,
            CancellationToken ct = default, IProgress<float> onProgress = null) where T : class
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentNullException(nameof(location));
            }

            // 调用方令牌已取消或模块已关闭：直接抛出取消，不再发起加载
            if (ct.IsCancellationRequested || isShutdown)
            {
                throw new OperationCanceledException(ct.IsCancellationRequested ? ct : CancellationToken.None);
            }

            string normalized = NormalizeLocation(location);
            Type assetType = typeof(T);
            AssetCacheKey key = new AssetCacheKey(normalized, assetType);

            // 单锁原子决策：缓存命中 / 加入等待列表 / 登记为首个加载者，三者互斥，
            // 避免双首调用竞态导致 pendingLoads 被覆盖。
            TaskCompletionSource<CachedAsset> waitTcs = null;
            lock (loadLock)
            {
                if (loadedAssets.TryGetValue(key, out CachedAsset cached))
                {
                    cached.RefCount++;
                    onProgress?.Report(1f);
                    return cached.Asset as T;
                }

                if (pendingLoads.TryGetValue(key, out List<TaskCompletionSource<CachedAsset>> tcsList))
                {
                    waitTcs = new TaskCompletionSource<CachedAsset>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    tcsList.Add(waitTcs);
                }
                else
                {
                    pendingLoads[key] = new List<TaskCompletionSource<CachedAsset>>();
                }
            }

            // 等待者：仅操作本等待者 TCS，注册调用方与模块两层令牌，互不干扰共享加载
            if (waitTcs != null)
            {
                onProgress?.Report(0f);
                using (ct.Register(() => waitTcs.TrySetCanceled()))
                using (moduleCts.Token.Register(() => waitTcs.TrySetCanceled()))
                {
                    try
                    {
                        CachedAsset result = await waitTcs.Task;
                        lock (loadLock)
                        {
                            result.RefCount++;
                        }

                        onProgress?.Report(1f);
                        return result.Asset as T;
                    }
                    catch (OperationCanceledException)
                    {
                        // 调用方或模块关闭取消，直接重抛（不影响共享加载与引用计数）
                        throw;
                    }
                }
            }

            // 首个加载者：合并调用方 ct 与模块关闭令牌，确保关闭/取消时底层真正终止
            CancellationTokenSource linkedCts = null;
            CancellationToken effectiveCt = ct;
            if (ct != CancellationToken.None)
            {
                linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, moduleCts.Token);
                effectiveCt = linkedCts.Token;
            }
            else
            {
                effectiveCt = moduleCts.Token;
            }

            try
            {
                // 透传 effectiveCt：调用方或模块关闭取消时由底层在合适时机抛出
                object asset = await helper.LoadAssetAsync(
                    location, assetType, priority, effectiveCt, onProgress);

                // 底层加载完成后才发生取消/关闭：释放资源并抛取消，避免写回已清空的缓存（缓存"复活"）
                if (isShutdown || ct.IsCancellationRequested || moduleCts.IsCancellationRequested)
                {
                    if (asset != null)
                    {
                        helper?.ReleaseAsset(location, assetType);
                    }

                    throw new OperationCanceledException();
                }

                if (asset != null)
                {
                    if (!(asset is T))
                    {
                        helper?.ReleaseAsset(location, assetType);
                        throw new RFrameworkException(
                            $"LoadAssetAsync<{assetType.Name}> returned incompatible asset type " +
                            $"'{asset.GetType().Name}' for: {location}");
                    }

                    CachedAsset newCached = new CachedAsset
                    {
                        Location = location,
                        Asset = asset,
                        AssetType = assetType,
                        RefCount = 1 // 初始引用计数为 1（当前调用者）
                    };

                    List<TaskCompletionSource<CachedAsset>> waiters = StoreLoadedAssetAndDetachWaiters(key, newCached);
                    CompleteWaiters(waiters, newCached, null, false);

                    return newCached.Asset as T;
                }

                // 加载返回 null：视为失败，catch 会统一分发失败事件并通知等待者。
                RFrameworkException loadException = new RFrameworkException(
                    $"LoadAssetAsync<{assetType.Name}> failed: {location}");
                CompleteWaiters(DetachPendingLoads(key), null, loadException, false);
                throw loadException;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    // 取消：仅将等待者一并取消，不视为加载失败，不派发失败事件
                    CompleteWaiters(DetachPendingLoads(key), null, null, true);
                }
                else
                {
                    // 先完成等待者，再分发外部事件。事件订阅者异常不应使并发调用方永久等待。
                    CompleteWaiters(DetachPendingLoads(key), null, ex, false);
                    FireLoadFailedEvent<T>(location, ex.Message);
                }

                throw;
            }
            finally
            {
                linkedCts?.Dispose();
            }
        }

        /// <summary>
        /// 同步加载资源。由 Runtime 辅助器内部阻塞等待异步完成。
        /// 仅在确实需要同步加载时使用，不建议在热更层调用。
        /// </summary>
        /// <typeparam name="T">资源类型。</typeparam>
        /// <param name="location">资源路径。</param>
        /// <returns>加载的资源对象。</returns>
        public T LoadAssetSync<T>(string location) where T : class
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentNullException(nameof(location));
            }

            string normalized = NormalizeLocation(location);
            AssetCacheKey key = new AssetCacheKey(normalized, typeof(T));

            // 检查缓存（复合键含类型，天然校验类型）
            lock (loadLock)
            {
                if (loadedAssets.TryGetValue(key, out CachedAsset cached))
                {
                    cached.RefCount++;
                    return cached.Asset as T;
                }

                if (pendingLoads.ContainsKey(key))
                {
                    throw new RFrameworkException(
                        "LoadAssetSync can not run while the same asset is loading asynchronously. Await LoadAssetAsync instead.");
                }
            }

            object asset = helper.LoadAssetSync(location, typeof(T));

            if (asset == null)
            {
                FireLoadFailedEvent<T>(location, $"LoadAssetSync<{typeof(T).Name}> failed: {location}");
                throw new RFrameworkException(
                    $"LoadAssetSync<{typeof(T).Name}> failed: {location}");
            }

            if (!(asset is T))
            {
                helper.ReleaseAsset(location, typeof(T));
                FireLoadFailedEvent<T>(location,
                    $"LoadAssetSync<{typeof(T).Name}> returned incompatible asset type: {location}");
                throw new RFrameworkException(
                    $"LoadAssetSync<{typeof(T).Name}> returned incompatible asset type " +
                    $"'{asset.GetType().Name}' for: {location}");
            }

            CachedAsset newCached = new CachedAsset
            {
                Location = location,
                Asset = asset,
                AssetType = typeof(T),
                RefCount = 1
            };

            lock (loadLock)
            {
                loadedAssets[key] = newCached;
            }

            return newCached.Asset as T;
        }

        // ==================== 场景加载 ====================

        /// <summary>
        /// 异步加载场景。
        /// </summary>
        /// <param name="location">场景资源路径。</param>
        /// <param name="sceneMode">场景加载模式：0=Single 替换当前场景，1=Additive 叠加到当前场景（与 UnityEngine.SceneManagement.LoadSceneMode 值一致）。</param>
        /// <param name="activateOnLoad">是否加载完成后立即激活。</param>
        /// <param name="priority">加载优先级。</param>
        /// <param name="onProgress">进度回调（0~1），可为 null。</param>
        public async Task LoadSceneAsync(string location, int sceneMode = 0,
            bool activateOnLoad = true, uint priority = 0, IProgress<float> onProgress = null)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentNullException(nameof(location));
            }

            try
            {
                await helper.LoadSceneAsync(location, sceneMode, activateOnLoad, priority, onProgress);
                if (sceneMode == 0)
                {
                    loadedScenes.Clear();
                }
                loadedScenes.Add(location);
            }
            catch (Exception ex)
            {
                SceneLoadFailedEvent failedEvent = new SceneLoadFailedEvent(location, ex.Message);
                GetEventModule()?.FireSafely(failedEvent);
                throw;
            }
        }

        /// <summary>
        /// 异步卸载场景。
        /// </summary>
        /// <param name="location">场景资源路径。</param>
        public async Task UnloadSceneAsync(string location)
        {
            EnsureInitialized();

            await helper.UnloadSceneAsync(location);
            loadedScenes.Remove(location);
        }

        // ==================== 资源卸载 ====================

        /// <summary>
        /// 卸载资源，引用计数 -1。当计数归零时加入待回收队列，下一帧由 Update 释放。
        /// </summary>
        /// <param name="asset">LoadAssetAsync / LoadAssetSync 返回的资源对象。</param>
        public void UnloadAsset(object asset)
        {
            if (asset == null)
            {
                return;
            }

            // 查找对应的 CachedAsset
            lock (loadLock)
            {
                AssetCacheKey matchedKey = default;
                CachedAsset matchedAsset = null;
                foreach (KeyValuePair<AssetCacheKey, CachedAsset> kv in loadedAssets)
                {
                    if (kv.Value.Asset == asset)
                    {
                        if (matchedAsset != null)
                        {
                            throw new RFrameworkException(
                                "UnloadAsset(object) is ambiguous for this asset. Use UnloadAsset<T>(location).");
                        }

                        matchedKey = kv.Key;
                        matchedAsset = kv.Value;
                    }
                }

                if (matchedAsset != null)
                {
                    ReleaseReferenceUnsafe(matchedKey, matchedAsset);
                }
            }
        }

        /// <inheritdoc/>
        public void UnloadAsset<T>(string location) where T : class
        {
            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentNullException(nameof(location));
            }

            AssetCacheKey key = new AssetCacheKey(NormalizeLocation(location), typeof(T));
            lock (loadLock)
            {
                if (!loadedAssets.TryGetValue(key, out CachedAsset asset))
                {
                    throw new RFrameworkException(
                        string.Format("Resource '{0}' with type '{1}' is not loaded.", location, typeof(T).FullName));
                }

                ReleaseReferenceUnsafe(key, asset);
            }
        }

        /// <summary>
        /// 立即释放所有引用计数为 0 的资源。
        /// 同时处理待回收队列中的资源。
        /// </summary>
        public void UnloadUnusedAssets()
        {
            // 收集所有引用计数为 0 的资源
            List<CachedAsset> toRelease = new List<CachedAsset>();
            lock (loadLock)
            {
                foreach (KeyValuePair<AssetCacheKey, CachedAsset> kv in loadedAssets)
                {
                    if (kv.Value.CanRelease)
                    {
                        toRelease.Add(kv.Value);
                    }
                }

                foreach (CachedAsset asset in toRelease)
                {
                    loadedAssets.Remove(new AssetCacheKey(NormalizeLocation(asset.Location), asset.AssetType));
                }
            }

            // 同时处理待回收队列
            lock (loadLock)
            {
                while (releaseQueue.Count > 0)
                {
                    CachedAsset asset = releaseQueue.Dequeue();
                    if (asset.CanRelease)
                    {
                        toRelease.Add(asset);
                    }
                }
            }

            for (int i = 0; i < toRelease.Count; i++)
            {
                ReleaseCachedAsset(toRelease[i]);
            }
        }

        // ==================== 查询 ====================

        /// <summary>
        /// 检查资源是否存在且可用。
        /// </summary>
        /// <param name="location">资源路径。</param>
        /// <returns>存在返回 true，否则返回 false。</returns>
        public bool HasAsset(string location)
        {
            EnsureInitialized();

            string normalized = NormalizeLocation(location);
            lock (loadLock)
            {
                foreach (KeyValuePair<AssetCacheKey, CachedAsset> kv in loadedAssets)
                {
                    if (kv.Key.Location == normalized)
                    {
                        return true;
                    }
                }
            }

            return helper.IsLocationValid(location);
        }

        /// <summary>
        /// 获取指定资源的下载大小（字节），用于判断是否需要下载。
        /// </summary>
        /// <param name="location">资源路径。</param>
        /// <returns>下载大小（字节）。</returns>
        public long GetDownloadSize(string location)
        {
            EnsureInitialized();
            return helper.GetDownloadSize(location);
        }

        /// <summary>
        /// 获取当前已加载资源的数量。
        /// </summary>
        public int LoadedAssetCount
        {
            get
            {
                lock (loadLock)
                {
                    return loadedAssets.Count;
                }
            }
        }

        /// <summary>
        /// 获取待加载资源数量（正在异步加载中的资源数）。
        /// </summary>
        public int LoadingAssetCount
        {
            get
            {
                lock (loadLock)
                {
                    return pendingLoads.Count;
                }
            }
        }

        // ==================== RFrameworkModule 生命周期 ====================

        /// <summary>
        /// 每帧轮询，处理待回收资源的释放。
        /// 每帧最多释放 5 个，避免尖峰。
        /// </summary>
        /// <param name="elapseSeconds">逻辑流逝时间。</param>
        /// <param name="realElapseSeconds">真实流逝时间。</param>
        internal override void Tick(float elapseSeconds, float realElapseSeconds)
        {
            // 处理待回收队列——每帧最多释放 5 个，避免尖峰
            List<CachedAsset> toRelease = new List<CachedAsset>(5);
            lock (loadLock)
            {
                int maxReleasePerFrame = 5;
                while (releaseQueue.Count > 0 && maxReleasePerFrame > 0)
                {
                    CachedAsset asset = releaseQueue.Dequeue();
                    if (asset.CanRelease)
                    {
                        toRelease.Add(asset);
                    }

                    maxReleasePerFrame--;
                }
            }

            foreach (CachedAsset asset in toRelease)
            {
                ReleaseCachedAsset(asset);
            }
        }

        /// <summary>
        /// 关闭并清理资源模块。释放所有缓存资源，销毁底层资源系统。
        /// </summary>
        internal override void Stop()
        {
            // 先标记关闭并取消模块级令牌：令所有在飞加载（含底层资源加载续体）
            // 立即感知取消，避免关闭后写入已清空的缓存表（缓存"复活"）。
            isShutdown = true;
            moduleCts.Cancel();

            // 释放所有缓存的资源句柄
            List<CachedAsset> assetsToRelease;
            lock (loadLock)
            {
                assetsToRelease = new List<CachedAsset>(loadedAssets.Values);
                loadedAssets.Clear();
                pendingLoads.Clear();
                releaseQueue.Clear();
            }

            foreach (CachedAsset asset in assetsToRelease)
            {
                ReleaseCachedAsset(asset);
            }

            loadedScenes.Clear();

            // 销毁底层资源系统（释放所有场景句柄、移除资源包裹并销毁辅助器）。
            helper?.Destroy();
            isInitialized = false;
            initializationTask = null;

            moduleCts.Dispose();
        }

        // ==================== 私有方法 ====================

        /// <summary>
        /// 检查模块是否已初始化，未初始化时抛出异常。
        /// </summary>
        private void EnsureInitialized()
        {
            if (!isInitialized)
            {
                throw new InvalidOperationException(
                    "ResourceModule: Not initialized. Call InitializeAsync() first.");
            }
        }

        /// <summary>
        /// 检查辅助器是否已设置，未设置时抛出异常。
        /// </summary>
        private void EnsureHelper()
        {
            if (helper == null)
            {
                throw new RFrameworkException("ResourceModule: Helper not set. Call SetHelper() before InitializeAsync.");
            }
        }

        /// <summary>
        /// 规范化资源路径作为缓存键：去除扩展名，使 "A" 与 "A.prefab" 映射到同一条缓存记录。
        /// 仅当最后一个点号位于路径分隔符之后时才去除（避免误伤带点的目录名）。
        /// 纯 C# 实现，不依赖 Unity API；仅用于框架缓存键，不影响传给底层辅助器的原始路径。
        /// </summary>
        private static string NormalizeLocation(string location)
        {
            if (string.IsNullOrEmpty(location))
            {
                return location;
            }

            int dot = location.LastIndexOf('.');
            int separator = Math.Max(location.LastIndexOf('/'), location.LastIndexOf('\\'));
            if (dot > separator)
            {
                return location.Substring(0, dot);
            }

            return location;
        }

        private void ReleaseReferenceUnsafe(AssetCacheKey key, CachedAsset asset)
        {
            asset.RefCount--;
            if (asset.RefCount <= 0)
            {
                loadedAssets.Remove(key);
                releaseQueue.Enqueue(asset);
            }
        }

        /// <summary>
        /// 释放缓存的资源对象，通知辅助器释放底层资源并清空引用。
        /// </summary>
        /// <param name="asset">要释放的缓存资源。</param>
        private void ReleaseCachedAsset(CachedAsset asset)
        {
            if (asset == null)
            {
                return;
            }

            helper?.ReleaseAsset(asset.Location, asset.AssetType);

            asset.Asset = null;
        }

        /// <summary>
        /// 原子写入缓存并摘除等待者。后续请求只能看到已缓存结果或新的加载批次，
        /// 不会加入一个即将完成但未收到通知的旧等待列表。
        /// </summary>
        private List<TaskCompletionSource<CachedAsset>> StoreLoadedAssetAndDetachWaiters(
            AssetCacheKey key, CachedAsset asset)
        {
            lock (loadLock)
            {
                loadedAssets[key] = asset;
                return DetachPendingLoadsUnsafe(key);
            }
        }

        /// <summary>
        /// 从并发加载表中摘除指定资源的全部等待者。
        /// </summary>
        private List<TaskCompletionSource<CachedAsset>> DetachPendingLoads(AssetCacheKey key)
        {
            lock (loadLock)
            {
                return DetachPendingLoadsUnsafe(key);
            }
        }

        private List<TaskCompletionSource<CachedAsset>> DetachPendingLoadsUnsafe(AssetCacheKey key)
        {
            if (!pendingLoads.TryGetValue(key, out List<TaskCompletionSource<CachedAsset>> waiters))
            {
                return null;
            }

            pendingLoads.Remove(key);
            return waiters;
        }

        /// <summary>
        /// 在锁外完成等待者，避免用户 continuation 重入资源模块时占用 loadLock。
        /// </summary>
        private static void CompleteWaiters(List<TaskCompletionSource<CachedAsset>> waiters,
            CachedAsset result, Exception exception, bool canceled)
        {
            if (waiters == null)
            {
                return;
            }

            foreach (TaskCompletionSource<CachedAsset> waiter in waiters)
            {
                if (canceled)
                {
                    waiter.TrySetCanceled();
                }
                else if (exception != null)
                {
                    waiter.TrySetException(exception);
                }
                else
                {
                    waiter.TrySetResult(result);
                }
            }
        }

        /// <summary>
        /// 惰性获取事件模块引用。
        /// 通过 RFrameworkModuleHost.Get 获取，若事件模块尚未就绪则返回 null。
        /// </summary>
        /// <returns>事件模块实例，未就绪时为 null。</returns>
        private IEventModule GetEventModule()
        {
            if (eventModule == null)
            {
                try
                {
                    eventModule = RFrameworkModuleHost.Get<IEventModule>();
                }
                catch
                {
                    // 事件模块未就绪时静默处理，不影响核心加载流程
                    return null;
                }
            }

            return eventModule;
        }

        /// <summary>
        /// 分发资源加载失败事件。
        /// 事件模块未就绪时静默跳过。
        /// </summary>
        /// <typeparam name="T">资源类型。</typeparam>
        /// <param name="location">资源路径。</param>
        /// <param name="errorMessage">失败原因。</param>
        private void FireLoadFailedEvent<T>(string location, string errorMessage)
        {
            IEventModule evt = GetEventModule();
            if (evt == null)
            {
                return;
            }

            ResourceLoadFailedEvent failedEvent = new ResourceLoadFailedEvent(location, typeof(T), errorMessage);
            evt.FireSafely(failedEvent);
        }
    }
}
