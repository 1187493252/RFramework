using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace RFramework
{
    /// <summary>
    /// UI 模块核心实现。维护单一窗口栈、层级排序、全屏遮挡和异步加载生命周期。
    /// </summary>
    internal sealed class UIModule : RFrameworkModule, IUIModule
    {
        /// <summary>
        /// UI 辅助器。
        /// </summary>
        private IUIHelper uiHelper;

        /// <summary>
        /// 资源模块引用。
        /// </summary>
        private IResourceModule resourceModule;

        /// <summary>
        /// 事件模块引用。
        /// </summary>
        private IEventModule eventModule;

        /// <summary>
        /// 窗口栈（按层级排序存储）。
        /// </summary>
        private readonly List<IUIForm> windowStack = new List<IUIForm>();

        /// <summary>
        /// 已打开 UI 的索引（assetName → IUIForm）。
        /// </summary>
        private readonly Dictionary<string, IUIForm> uiForms = new Dictionary<string, IUIForm>();

        /// <summary>
        /// 框架加载并持有的 UI 资源。关闭窗口时按资源名称归还对应引用。
        /// </summary>
        private readonly Dictionary<string, object> uiAssets = new Dictionary<string, object>();

        /// <summary>
        /// 由场景或业务代码创建并持有的外部 UI 名称集合。
        /// 外部 UI 仅由模块代管生命周期，不释放实例或资源。
        /// </summary>
        private readonly HashSet<string> externalUIForms = new HashSet<string>();

        private readonly HashSet<IUIForm> pausedUIForms = new HashSet<IUIForm>();

        /// <summary>
        /// 正在加载中的 UI 资源路径集合。
        /// </summary>
        private readonly HashSet<string> loadingUIForms = new HashSet<string>();

        /// <summary>
        /// 正在关闭（加载中被取消）的 UI 资源路径集合。
        /// CloseUIForm 在 UI 仍加载时记录，OpenUIFormAsync 加载完成后据此放弃打开。
        /// </summary>
        private readonly HashSet<string> abortedUIForms = new HashSet<string>();

        /// <summary>
        /// 模块是否已关闭。关闭后加载完成的 UI 不再打开。
        /// </summary>
        private bool isShutdown;

        /// <summary>
        /// 获取框架模块优先级。
        /// UIModule Priority=30，在 Entity(25) 之后。
        /// </summary>
        internal override int Order
        {
            get
            {
                return 30;
            }
        }

        /// <summary>
        /// 获取当前打开的 UI 数量。
        /// </summary>
        public int UIFormCount => uiForms.Count;

        /// <summary>
        /// 设置 UI 辅助器。
        /// </summary>
        public void SetHelper(IUIHelper helper)
        {
            uiHelper = helper;
        }

        /// <summary>
        /// 设置依赖模块引用。
        /// </summary>
        public void SetDependencies(IResourceModule resourceModule, IEventModule eventModule)
        {
            this.resourceModule = resourceModule;
            this.eventModule = eventModule;
        }

        /// <summary>
        /// 异步打开 UI。
        /// </summary>
        public async Task<IUIForm> OpenUIFormAsync(string assetName, int windowLayer = 0,
            bool fullScreen = false, uint priority = 0, object userData = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(assetName))
            {
                throw new RFrameworkException("UI asset name is invalid.");
            }

            if (uiHelper == null)
            {
                throw new RFrameworkException("UI helper is not set.");
            }

            if (resourceModule == null)
            {
                throw new RFrameworkException("Resource module is not set.");
            }

            if (uiForms.ContainsKey(assetName))
            {
                throw new RFrameworkException($"UI form '{assetName}' is already opened.");
            }

            if (loadingUIForms.Contains(assetName))
            {
                throw new RFrameworkException($"UI form '{assetName}' is already loading.");
            }

            double startTimestamp = DateTime.UtcNow.Ticks;
            loadingUIForms.Add(assetName);

            object uiAsset = null;
            object uiInstance = null;
            IUIForm uiForm = null;
            try
            {
                // 通过 IResourceModule 加载 UI Prefab
                uiAsset = await resourceModule.LoadAssetAsync<object>(assetName, priority, ct);

                loadingUIForms.Remove(assetName);

                // 加载完成后再次校验：取消令牌触发 / 模块已关闭 / 加载期间被 CloseUIForm 取消。
                // 任一成立则放弃打开，并释放已加载但未使用的资源引用计数，避免泄漏。
                if (ct.IsCancellationRequested || isShutdown || abortedUIForms.Remove(assetName))
                {
                    resourceModule.UnloadAsset<object>(assetName);
                    uiAsset = null;
                    throw new OperationCanceledException();
                }

                // 实例化 UI 对象
                uiInstance = uiHelper.InstantiateUI(uiAsset);
                uiForm = uiHelper.CreateUIForm(uiInstance, assetName, windowLayer, fullScreen);
                if (uiForm == null)
                {
                    throw new RFrameworkException($"UI helper failed to create UI form '{assetName}'.");
                }

                // 生命周期：OnInit → OnOpen
                uiForm.OnInit(userData);
                uiForms.Add(assetName, uiForm);
                uiAssets.Add(assetName, uiAsset);

                // 按层级插入窗口栈
                InsertToStack(uiForm);

                uiForm.OnOpen(userData);

                // 全屏判定：隐藏被覆盖的窗口
                ApplyFullScreenVisibility();

                float duration = (float)(DateTime.UtcNow.Ticks - startTimestamp) / 10000000f;

                if (eventModule != null)
                {
                    eventModule.FireSafely(new OpenUIFormSuccessEvent(assetName, uiForm, duration, userData));
                }

                return uiForm;
            }
            catch (Exception ex)
            {
                if (uiForm != null)
                {
                    uiForms.Remove(assetName);
                    windowStack.Remove(uiForm);
                    pausedUIForms.Remove(uiForm);
                }

                uiAssets.Remove(assetName);

                if (uiInstance != null)
                {
                    uiHelper?.ReleaseUI(uiInstance);
                }

                if (uiAsset != null)
                {
                    resourceModule?.UnloadAsset<object>(assetName);
                }

                loadingUIForms.Remove(assetName);
                abortedUIForms.Remove(assetName);

                // 取消（含加载中关闭）属正常流程，不视为失败事件
                if (!(ex is OperationCanceledException) && eventModule != null)
                {
                    eventModule.FireSafely(new OpenUIFormFailureEvent(assetName, ex.Message, userData));
                }

                throw;
            }
        }

        /// <summary>
        /// 登记由外部创建并持有的 UI 表单。
        /// </summary>
        /// <param name="formName">UI 表单唯一名称。</param>
        /// <param name="uiForm">已创建的 UI 表单。</param>
        /// <param name="userData">用户自定义数据。</param>
        public void RegisterUIForm(string formName, IUIForm uiForm, object userData = null)
        {
            if (isShutdown)
            {
                throw new RFrameworkException("UI module is shutdown.");
            }

            if (string.IsNullOrEmpty(formName))
            {
                throw new RFrameworkException("UI form name is invalid.");
            }

            if (uiForm == null)
            {
                throw new RFrameworkException("UI form is invalid.");
            }

            if (!string.Equals(uiForm.AssetName, formName, StringComparison.Ordinal))
            {
                throw new RFrameworkException(
                    $"UI form name '{formName}' does not match form asset name '{uiForm.AssetName}'.");
            }

            if (uiForms.ContainsKey(formName) || loadingUIForms.Contains(formName))
            {
                throw new RFrameworkException($"UI form '{formName}' is already opened or loading.");
            }

            if (uiForms.ContainsValue(uiForm))
            {
                throw new RFrameworkException("UI form instance is already registered.");
            }

            double startTimestamp = DateTime.UtcNow.Ticks;
            try
            {
                uiForm.OnInit(userData);
                uiForms.Add(formName, uiForm);
                externalUIForms.Add(formName);
                InsertToStack(uiForm);
                uiForm.OnOpen(userData);
                ApplyFullScreenVisibility();

                if (eventModule != null)
                {
                    float duration = (float)(DateTime.UtcNow.Ticks - startTimestamp) / 10000000f;
                    eventModule.FireSafely(new OpenUIFormSuccessEvent(formName, uiForm, duration, userData));
                }
            }
            catch (Exception ex)
            {
                uiForms.Remove(formName);
                externalUIForms.Remove(formName);
                windowStack.Remove(uiForm);
                pausedUIForms.Remove(uiForm);
                ApplyFullScreenVisibility();

                if (eventModule != null)
                {
                    eventModule.FireSafely(new OpenUIFormFailureEvent(formName, ex.Message, userData));
                }

                throw;
            }
        }

        /// <summary>
        /// 注销由外部创建并持有的 UI 表单。
        /// </summary>
        /// <param name="formName">UI 表单唯一名称。</param>
        /// <param name="userData">用户自定义数据。</param>
        public void UnregisterUIForm(string formName, object userData = null)
        {
            if (!externalUIForms.Contains(formName))
            {
                return;
            }

            CloseUIForm(formName, userData);
        }

        /// <summary>
        /// 关闭 UI。
        /// </summary>
        public void CloseUIForm(string assetName, object userData = null)
        {
            // 若 UI 仍在加载中：标记取消，待加载完成后由 OpenUIFormAsync 放弃打开
            if (loadingUIForms.Contains(assetName))
            {
                abortedUIForms.Add(assetName);
                return;
            }

            if (!uiForms.TryGetValue(assetName, out IUIForm uiForm))
            {
                return;
            }

            bool isExternal = externalUIForms.Remove(assetName);

            Exception closeFailure = null;
            try
            {
                uiForm.OnClose(userData);
            }
            catch (Exception ex)
            {
                closeFailure = ex;
            }
            finally
            {
                uiForms.Remove(assetName);
                windowStack.Remove(uiForm);
                pausedUIForms.Remove(uiForm);

                // 外部 UI 由场景或业务代码持有，模块只注销，不释放实例和资源。
                if (!isExternal)
                {
                    uiHelper.ReleaseUI(uiForm.Handle);
                    if (uiAssets.Remove(assetName))
                    {
                        resourceModule.UnloadAsset<object>(assetName);
                    }
                }

                ApplyFullScreenVisibility();
            }

            if (eventModule != null)
            {
                eventModule.FireSafely(new CloseUIFormCompleteEvent(assetName, userData));
            }

            if (closeFailure != null)
            {
                ExceptionDispatchInfo.Capture(closeFailure).Throw();
            }
        }

        /// <summary>
        /// 关闭所有已打开的 UI。
        /// </summary>
        public void CloseAllUIForms(object userData = null)
        {
            // 保留加载记录，直到异步续体消费取消标记，避免迟到的加载重新打开窗口。
            foreach (string assetName in loadingUIForms)
            {
                abortedUIForms.Add(assetName);
            }

            Exception firstFailure = null;
            while (windowStack.Count > 0)
            {
                string assetName = windowStack[windowStack.Count - 1].AssetName;
                try
                {
                    CloseUIForm(assetName, userData);
                }
                catch (Exception ex)
                {
                    firstFailure ??= ex;
                }
            }

            windowStack.Clear();
            uiForms.Clear();
            uiAssets.Clear();
            externalUIForms.Clear();

            if (firstFailure != null)
            {
                ExceptionDispatchInfo.Capture(firstFailure).Throw();
            }
        }

        /// <summary>
        /// 判断 UI 是否已打开。
        /// </summary>
        public bool HasUIForm(string assetName)
        {
            return uiForms.ContainsKey(assetName);
        }

        /// <summary>
        /// 获取 UI 表单。
        /// </summary>
        public IUIForm GetUIForm(string assetName)
        {
            if (uiForms.TryGetValue(assetName, out IUIForm uiForm))
            {
                return uiForm;
            }

            return null;
        }

        /// <summary>
        /// 获取当前窗口栈顶的 UI。
        /// </summary>
        public IUIForm GetTopUIForm()
        {
            return windowStack.Count > 0 ? windowStack[windowStack.Count - 1] : null;
        }

        /// <summary>
        /// 关闭当前窗口栈顶的 UI。
        /// </summary>
        public bool CloseTopUIForm(object userData = null)
        {
            IUIForm topUIForm = GetTopUIForm();
            if (topUIForm == null)
            {
                return false;
            }

            CloseUIForm(topUIForm.AssetName, userData);
            return true;
        }

        /// <summary>
        /// 获取所有已打开的 UI。
        /// </summary>
        public IUIForm[] GetAllUIForms()
        {
            IUIForm[] results = new IUIForm[windowStack.Count];
            for (int i = 0; i < windowStack.Count; i++)
            {
                results[i] = windowStack[i];
            }

            return results;
        }

        /// <summary>
        /// 获取所有正在加载中的 UI 资源路径。
        /// </summary>
        public string[] GetAllLoadingUIFormAssetNames()
        {
            string[] results = new string[loadingUIForms.Count];
            int index = 0;
            foreach (string assetName in loadingUIForms)
            {
                results[index++] = assetName;
            }

            return results;
        }

        /// <summary>
        /// 判断 UI 是否正在加载中。
        /// </summary>
        public bool IsLoadingUIForm(string assetName)
        {
            return loadingUIForms.Contains(assetName);
        }

        /// <summary>
        /// 按层级将窗口插入栈中的正确位置。
        /// 同层窗口按插入顺序排列（后插入的在后面，渲染在更上层）。
        /// </summary>
        private void InsertToStack(IUIForm uiForm)
        {
            int targetLayer = uiForm.WindowLayer;
            int insertIndex = windowStack.Count;

            // 找到同层或更高层的插入位置
            for (int i = 0; i < windowStack.Count; i++)
            {
                if (windowStack[i].WindowLayer > targetLayer)
                {
                    insertIndex = i;
                    break;
                }
            }

            windowStack.Insert(insertIndex, uiForm);
        }

        /// <summary>
        /// 应用全屏窗口可见性规则。
        /// 从栈顶往下扫描，遇到 FullScreen 窗口后将其余窗口设为暂停。
        /// </summary>
        private void ApplyFullScreenVisibility()
        {
            bool shouldPause = false;

            for (int i = windowStack.Count - 1; i >= 0; i--)
            {
                IUIForm uiForm = windowStack[i];

                if (shouldPause)
                {
                    // 被全屏窗口覆盖：暂停
                    if (uiForm.IsOpened && pausedUIForms.Add(uiForm))
                    {
                        uiForm.OnPause();
                    }
                }
                else if (pausedUIForms.Remove(uiForm))
                {
                    // 只恢复确实由本模块暂停过的窗口。
                    uiForm.OnResume();
                }

                // 先恢复当前窗口，再让其全屏标记影响后续下层窗口。
                shouldPause |= uiForm.FullScreen;
            }
        }

        /// <summary>
        /// 模块轮询更新。驱动所有已打开 UI 的 OnUpdate。
        /// </summary>
        internal override void Tick(float elapseSeconds, float realElapseSeconds)
        {
            for (int i = 0; i < windowStack.Count; i++)
            {
                IUIForm uiForm = windowStack[i];
                if (!pausedUIForms.Contains(uiForm))
                {
                    uiForm.OnUpdate(elapseSeconds, realElapseSeconds);
                }
            }
        }

        /// <summary>
        /// 模块关闭。关闭所有 UI 并清理状态。
        /// </summary>
        internal override void Stop()
        {
            isShutdown = true;
            CloseAllUIForms();
            windowStack.Clear();
            uiForms.Clear();
            uiAssets.Clear();
            externalUIForms.Clear();
            pausedUIForms.Clear();
            loadingUIForms.Clear();
            abortedUIForms.Clear();
        }
    }
}
