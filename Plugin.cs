using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NTRPovCam
{
    /// <summary>
    /// NTR Master POV 相机插件 v2.3（菜单版）
    ///
    /// 架构说明（沿用 v2.2 结论）：
    /// - BepInEx chainloader 创建的组件 Unity 消息(Start/Update/OnGUI)不运行（国内版 Mono），
    ///   全部逻辑由 Harmony postfix 挂 GameManagement.LateUpdate 驱动。
    /// - 屏幕绘制（菜单/提示）走运行时动态创建的 PovNoticeGui.OnGUI（已验证可跑）。
    /// v2.3 变更：
    /// - F10 改为呼出菜单（不再直接开关 POV），菜单里选人物/开自由飞行/隐藏头部开关。
    /// - 新增"隐藏头部"配置项，可在菜单或 F1 面板中切换。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class PovCamPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ntr.povcam";
        public const string PluginName = "NTR POV Free Camera";
        public const string PluginVersion = "2.4.0";

        // ---- 配置（ConfigurationManager F1 面板中可查看/修改）----
        private static ConfigEntry<KeyCode> CfgToggle;
        private static ConfigEntry<KeyCode> CfgCycle;
        private static ConfigEntry<KeyCode> CfgFly;
        private static ConfigEntry<KeyCode> CfgReset;
        private static ConfigEntry<bool> CfgHideHead;

        // ---- 参数 ----
        private static readonly Vector3 EyeOffset = new Vector3(0f, 0.05f, 0.08f);
        private const float LookSensitivity = 3f;
        private const float FlySpeed = 1.5f;
        private const float FastMultiplier = 4f;
        private const float PovNearClip = 0.01f;
        private static Transform currentHeadBone;
        private static Vector3 originalHeadScale = Vector3.one;
        private enum PovState { Off, Fpv, Fly }

        private class PovTarget
        {
            public UnitBase Unit;
            public Transform Head;
            public string DisplayName;
            public bool Inactive; // 人物当前被隐藏/未激活，仍可 POV 但可能静止
        }

        private static ManualLogSource SLog;
        private static PovState state = PovState.Off;
        private static float savedNearClip;
        private static float yawOff, pitchOff;
        private static readonly List<PovTarget> targets = new List<PovTarget>();
        private static PovTarget curTarget;
        private static readonly List<Renderer> hiddenRenderers = new List<Renderer>();
        private static PlayerControl activatedActorPlayer; // 被我们激活主角模型的原状态记录
        private static float savedFadeCutoff = -1f; // POV 期间归零前的主角遮挡渐隐强度

        // ---- 菜单 ----
        private static bool menuOpen;
        private static int selIdx;
        private static Vector2 menuScroll;
        private static Rect menuRect = new Rect(0f, 0f, 420f, 10f);
        private static bool menuRectInit;

        // ---- 屏幕提示 ----
        private static string notice;
        private static float noticeUntil;
        private static GameObject noticeGo;
        private static GUIStyle noticeStyle, statusStyle, selBtnStyle, menuLabelStyle;

        // ---- 诊断 ----
        private static bool tickLogged, tickErrLogged;

        private static CameraControl CC
        {
            get
            {
                var pc = PlayerControl.Self;
                return pc == null ? null : pc.cameraControl;
            }
        }

        public PovCamPlugin()
        {
            if (SLog == null)
            {
                SLog = Logger;
                SLog.LogInfo("[diag] ctor OK (v" + PluginVersion + ")");
            }
            CfgToggle = Config.Bind("Keys", "Open Menu", KeyCode.F10, "呼出/关闭 POV 菜单");
            CfgCycle = Config.Bind("Keys", "Cycle Character", KeyCode.F11, "POV 中快速切换人物");
            CfgFly = Config.Bind("Keys", "Free Fly", KeyCode.F12, "自由飞行开关");
            CfgReset = Config.Bind("Keys", "Reset View Offset", KeyCode.R, "重置视角偏移");
            CfgHideHead = Config.Bind("Options", "Hide Head", true, "第一人称时隐藏该人物的头部（防满屏后脑勺）");
        }

        private void Awake()
        {
            var harmony = new Harmony(PluginGuid);
            harmony.PatchAll(typeof(Patches));
            SLog.LogInfo("[diag] Awake OK | " + CfgToggle.Value + "=菜单  Harmony -> GameManagement.LateUpdate");
        }

        // ================================================================
        // Harmony 驱动的主循环（挂在游戏 GameManagement.LateUpdate 尾部）
        // ================================================================

        internal static void Tick()
        {
            try
            {
                if (!tickLogged)
                {
                    tickLogged = true;
                    SLog.LogInfo("[diag] Harmony Tick 运行中");
                }
                EnsureNoticeGui();

                // ---- 菜单开合 ----
                if (Input.GetKeyDown(CfgToggle.Value))
                {
                    if (menuOpen) CloseMenu(); else OpenMenu();
                }

                if (menuOpen)
                {
                    MenuKeyNav();
                }

                // ---- 自由飞行直通键 ----
                if (Input.GetKeyDown(CfgFly.Value))
                {
                    if (state == PovState.Fly) Shutdown();
                    else if (state == PovState.Off) EnterFly();
                }

                if (state == PovState.Off)
                {
                    return;
                }

                var cc = CC;
                if (cc == null || cc.mode != CameraControl.Mode.TakeOver)
                {
                    Abandon();
                    return;
                }

                // ---- POV 运行中的快捷键 ----
                if (Input.GetKeyDown(CfgCycle.Value) && state == PovState.Fpv)
                {
                    CycleTarget();
                    SyncSelToCurrent();
                    return;
                }

                if (Input.GetKeyDown(CfgReset.Value) && state == PovState.Fpv)
                {
                    InitFpvView();
                    ShowNotice("视角已重置到人物朝向");
                    return;
                }

                // ---- 视角旋转（菜单打开时不抢右键）----
                if (!menuOpen && Input.GetKey(KeyCode.Mouse1))
                {
                    yawOff += Input.GetAxis("Mouse X") * LookSensitivity;
                    pitchOff -= Input.GetAxis("Mouse Y") * LookSensitivity;
                    pitchOff = Mathf.Clamp(pitchOff, -85f, 85f);
                    if (state == PovState.Fly)
                    {
                        cc.trans.rotation = Quaternion.Euler(pitchOff, yawOff, 0f);
                    }
                }

                // ---- 自由飞行移动（菜单打开时暂停，避免误操作）----
                if (state == PovState.Fly && !menuOpen)
                {
                    Vector3 move = Vector3.zero;
                    if (Input.GetKey(KeyCode.W)) move += Vector3.forward;
                    if (Input.GetKey(KeyCode.S)) move -= Vector3.forward;
                    if (Input.GetKey(KeyCode.A)) move -= Vector3.right;
                    if (Input.GetKey(KeyCode.D)) move += Vector3.right;
                    if (Input.GetKey(KeyCode.E)) move += Vector3.up;
                    if (Input.GetKey(KeyCode.Q)) move -= Vector3.up;
                    if (move.sqrMagnitude > 0.001f)
                    {
                        float speed = FlySpeed * (Input.GetKey(KeyCode.LeftShift) ? FastMultiplier : 1f);
                        cc.trans.position += cc.trans.rotation * move.normalized * (speed * Time.deltaTime);
                    }
                }

                // ---- FOV 滚轮（菜单打开时不抢滚动条）----
                float scroll = Input.mouseScrollDelta.y;
                if (!menuOpen && Mathf.Abs(scroll) > 0.01f && cc.camera != null)
                {
                    cc.camera.fieldOfView = Mathf.Clamp(cc.camera.fieldOfView - scroll * 3f, 20f, 110f);
                }

                // ---- 第一人称贴头（动画算完之后）----
                // v2.4.0: 位置继续贴头跟随人物，但视角用绝对角度（同自由飞行），
                // 不再绑定头部动画朝向——人物转头/动画晃动不会带着镜头晃
                if (state == PovState.Fpv && curTarget != null && curTarget.Head != null)
                {
                    var head = curTarget.Head;
                    var t = cc.trans;

                    // 1. 先用正常尺寸算出眼部世界坐标
                    // 如果上一帧被缩成了0，先临时还原以确保 TransformPoint 坐标精确
                    if (CfgHideHead.Value)
                    {
                        head.localScale = originalHeadScale;
                    }

                    t.position = head.TransformPoint(EyeOffset);
                    t.rotation = Quaternion.Euler(pitchOff, yawOff, 0f);

                    // 2. 算完相机位置后，立即把头骨缩小到 0，隐藏头部与头发
                    if (CfgHideHead.Value)
                    {
                        head.localScale = Vector3.zero;
                    }
                }
            }
            catch (Exception e)
            {
                if (!tickErrLogged)
                {
                    tickErrLogged = true;
                    SLog.LogError("[diag] Tick 异常: " + e);
                }
            }
        }

        // ================================================================
        // 菜单
        // ================================================================

        private static void OpenMenu()
        {
            menuOpen = true;
            BuildTargets();
            SyncSelToCurrent();
            if (!menuRectInit)
            {
                menuRectInit = true;
                menuRect.x = Mathf.Max(20f, Screen.width * 0.5f - 210f);
                menuRect.y = Mathf.Max(60f, Screen.height * 0.5f - 260f);
            }
            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }
            SLog.LogInfo("POV 菜单已打开，人物数: " + targets.Count);
        }

        private static void CloseMenu()
        {
            menuOpen = false;
        }

        private static void SyncSelToCurrent()
        {
            if (curTarget != null)
            {
                int i = targets.FindIndex(t => t.Unit == curTarget.Unit);
                if (i >= 0) selIdx = i;
            }
            if (selIdx >= targets.Count) selIdx = Mathf.Max(0, targets.Count - 1);
        }

        private static void MenuKeyNav()
        {
            if (targets.Count == 0) { }
            else if (Input.GetKeyDown(KeyCode.UpArrow))
            {
                selIdx = (selIdx - 1 + targets.Count) % targets.Count;
                menuScroll.y = Mathf.Max(0f, menuScroll.y - 26f);
            }
            else if (Input.GetKeyDown(KeyCode.DownArrow))
            {
                selIdx = (selIdx + 1) % targets.Count;
                menuScroll.y += 26f;
            }
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                EnterFpvAt(selIdx);
                CloseMenu();
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                CloseMenu();
            }
        }

        // ================================================================
        // 状态切换
        // ================================================================

        private static void EnterFpvAt(int idx)
        {
            if (targets.Count == 0) BuildTargets();
            if (targets.Count == 0 || idx < 0 || idx >= targets.Count)
            {
                ShowNotice("POV: 场景内没有可切换的人物");
                return;
            }

            var cc = CC;
            if (cc == null || cc.camera == null)
            {
                ShowNotice("POV: 玩家/相机未就绪，进游戏后再试");
                return;
            }

            if (state == PovState.Off)
            {
                cc.TakeOver();
                savedNearClip = cc.camera.nearClipPlane;
                cc.camera.nearClipPlane = PovNearClip;
                var pc = PlayerControl.Self;
                if (pc != null) pc.FreeCameraCounter++;
            }
            // Fly -> Fpv 无需重新 TakeOver，只是换驱动方式

            // 若之前的主角处理（防淡出/藏头/激活模型）还在，先还原再对新目标处理
            RestoreRenderers();
            RestorePlayerActor();

            yawOff = 0f;
            pitchOff = 0f;
            state = PovState.Fpv;
            SetTarget(idx);
            ShowNotice("第一人称: " + curTarget.DisplayName + "  |  " + CfgToggle.Value + " 菜单");
        }

        private static void EnterFly()
        {
            var cc = CC;
            if (cc == null || cc.camera == null)
            {
                ShowNotice("POV: 玩家/相机未就绪，进游戏后再按");
                return;
            }

            Vector3 e = cc.trans.rotation.eulerAngles;
            yawOff = e.y;
            pitchOff = e.x > 180f ? e.x - 360f : e.x;

            cc.TakeOver();
            savedNearClip = cc.camera.nearClipPlane;
            cc.camera.nearClipPlane = PovNearClip;

            var pc = PlayerControl.Self;
            if (pc != null) pc.FreeCameraCounter++;

            RestoreRenderers();
            RestorePlayerActor();
            curTarget = null;

            // 飞行模式同样处理主角：激活模型 + 防淡出
            if (pc != null)
            {
                ApplyPlayerVisuals(pc);
            }

            // 飞行模式隐藏场景内所有人物的头部（主角+NPC）：镜头贴近任何身体都不被脑袋挡住
            if (CfgHideHead.Value)
            {
                RestoreRenderers();
                BuildTargets();
                int hidden = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (targets[i].Head != null)
                    {
                        HideHeadRenderers(targets[i].Head, false);
                        hidden++;
                    }
                }
                SLog.LogInfo("POV fly: 已隐藏 " + hidden + " 个人物的头部");
            }

            state = PovState.Fly;
            ShowNotice("自由飞行  |  右键转视角 WASD飞行 Shift加速  头部隐藏:" + (CfgHideHead.Value ? "开" : "关"));
        }

        private static void Shutdown()
        {
            RestoreRenderers();
            RestorePlayerActor();
            curTarget = null;

            var cc = CC;
            if (cc != null)
            {
                if (cc.camera != null) cc.camera.nearClipPlane = savedNearClip;
                if (cc.mode == CameraControl.Mode.TakeOver) cc.PrevMode(2f);
            }

            var pc = PlayerControl.Self;
            if (pc != null && pc.FreeCameraCounter > 0) pc.FreeCameraCounter--;

            state = PovState.Off;
            ShowNotice("POV: 关闭");
        }

        private static void Abandon()
        {
            RestoreRenderers();
            RestorePlayerActor();
            curTarget = null;

            var cc = CC;
            if (cc != null && cc.camera != null)
            {
                cc.camera.nearClipPlane = savedNearClip;
            }

            var pc = PlayerControl.Self;
            if (pc != null && pc.FreeCameraCounter > 0) pc.FreeCameraCounter--;

            state = PovState.Off;
            ShowNotice("相机被游戏回收，POV 自动关闭");
        }

        // ================================================================
        // 人物目标管理
        // ================================================================

        private static void CycleTarget()
        {
            RestoreRenderers();
            RestorePlayerActor();
            BuildTargets();
            if (targets.Count == 0)
            {
                ShowNotice("POV: 场景内没有可切换的人物");
                Shutdown();
                return;
            }

            int idx = 0;
            if (curTarget != null)
            {
                int cur = targets.FindIndex(t => t.Unit == curTarget.Unit);
                idx = cur < 0 ? 0 : (cur + 1) % targets.Count;
            }
            SetTarget(idx);
            ShowNotice("切换视角: " + curTarget.DisplayName);
        }

        private static void SetTarget(int idx)
        {
            curTarget = targets[idx];

            // 主角模型被游戏隐藏时，激活它让动画/头部跟随正常工作；同时防淡出
            ApplyPlayerVisuals(curTarget.Unit as PlayerControl);

            // v2.4.0: 视角初始化为人物头部当前朝向（之后完全自由，R 可重新对准）
            InitFpvView();

            if (CfgHideHead.Value)
            {
                HideHeadRenderers(curTarget.Head);
            }

            SLog.LogInfo(string.Format(
                "POV attach: {0} | head bone: {1} | head fwd: {2} | hideHead: {3}",
                curTarget.DisplayName, curTarget.Head.name, curTarget.Head.forward.ToString("F3"), CfgHideHead.Value));
        }

        /// <summary>菜单里切换"隐藏头部"后立即生效（不用等换人）</summary>
        private static void ApplyHideState()
        {
            if (state == PovState.Fpv && curTarget != null)
            {
                if (CfgHideHead.Value) HideHeadRenderers(curTarget.Head);
                else RestoreRenderers();
            }
            else if (state == PovState.Fly)
            {
                // 飞行模式对场景内全部人物生效
                RestoreRenderers();
                if (CfgHideHead.Value)
                {
                    BuildTargets();
                    for (int i = 0; i < targets.Count; i++)
                    {
                        if (targets[i].Head != null) HideHeadRenderers(targets[i].Head, false);
                    }
                }
            }
        }

        private static void BuildTargets()
        {
            targets.Clear();
            var seen = new HashSet<UnitBase>();

            void TryAdd(UnitBase u)
            {
                if (u == null || !seen.Add(u)) return;
                var head = ResolveHead(u);
                if (head == null) return;
                string name = null;
                try { name = u.UnitSetting != null ? u.UnitSetting.DisplayName : null; } catch { }
                if (string.IsNullOrEmpty(name)) name = u.gameObject.name;
                if (u is PlayerControl) name += " (主角)";
                bool inactive;
                try { inactive = !u.gameObject.activeInHierarchy; } catch { inactive = false; }
                targets.Add(new PovTarget { Unit = u, Head = head, DisplayName = name, Inactive = inactive });
            }

            TryAdd(PlayerControl.Self);
            try { if (GameManagement.Self != null && GameManagement.Self.Units != null) foreach (var u in GameManagement.Self.Units) TryAdd(u); } catch { }
            TryAdd(GirlfriendControl.Self);
            TryAdd(BoyfriendControl.Self);
            TryAdd(ServantControl.Self);

            // 兜底：全场景扫描（含被隐藏/未激活的人物，排除预制体资产）
            // 场景管理器列表可能漏掉 SetActive(false) 的对象（如隐藏的主角）
            try
            {
                var all = Resources.FindObjectsOfTypeAll<UnitBase>();
                for (int i = 0; i < all.Length; i++)
                {
                    var u = all[i];
                    if (u == null) continue;
                    var go = u.gameObject;
                    if (!go.scene.IsValid() || !go.scene.isLoaded) continue; // 跳过 prefab/资产
                    TryAdd(u);
                }
            }
            catch (Exception e)
            {
                SLog.LogInfo("POV 兜底扫描异常: " + e.Message);
            }

            // 主角结构诊断（定位"搜不到主控"问题用）
            try
            {
                var p = PlayerControl.Self;
                if (p == null)
                {
                    SLog.LogInfo("POV diag: PlayerControl.Self = null（当前场景无主角控制器）");
                }
                else
                {
                    var anims = p.GetComponentsInChildren<Animator>(true);
                    var sbA = new System.Text.StringBuilder();
                    for (int i = 0; i < anims.Length; i++)
                    {
                        if (anims[i] == null) continue;
                        sbA.Append(anims[i].name).Append("[human:").Append(anims[i].isHuman)
                           .Append(",active:").Append(anims[i].gameObject.activeSelf).Append("] ");
                    }
                    SLog.LogInfo("POV diag: Player active=" + p.gameObject.activeInHierarchy
                        + " Actor=" + (p.Actor != null ? p.Actor.name : "null")
                        + " actorActive=" + (p.Actor != null ? p.Actor.gameObject.activeSelf.ToString() : "-")
                        + " animators: " + sbA);
                }
            }
            catch (Exception e) { SLog.LogInfo("POV diag 异常: " + e.Message); }

            if (SLog != null)
            {
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < targets.Count; i++)
                    sb.Append(i).Append('.').Append(targets[i].DisplayName)
                      .Append(targets[i].Inactive ? "(未激活)" : "").Append("  ");
                SLog.LogInfo("POV targets: " + targets.Count + " -> " + sb);
            }
        }

        private static Transform ResolveHead(UnitBase u)
        {
            // 1) 遍历所有 Animator 找人形 rig 的头骨（含未激活）
            //    关键：主角的可见模型是 PlayerControl.Actor 子物体（PlayerActorControl），
            //    平时 SetActive(false)，只有动作状态才激活；它的 Animator 才是真人形 rig。
            //    PlayerControl 根上的 Animator 是代理 rig，GetBoneTransform 拿不到头。
            var animators = u.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                var a = animators[i];
                if (a == null) continue;
                try
                {
                    if (a.isHuman)
                    {
                        var hb = a.GetBoneTransform(HumanBodyBones.Head);
                        if (hb != null) return hb;
                    }
                }
                catch { }
            }

            // 2) 骨骼名兜底（含未激活）：name 含 "head"，精确等于 > 结尾 > 包含
            Transform best = null;
            int bestScore = int.MaxValue;
            var bones = u.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < bones.Length; i++)
            {
                string n = bones[i].name.ToLowerInvariant();
                if (!n.Contains("head")) continue;
                int score = bones[i].name.Length;
                if (n.Equals("head")) score -= 1000;
                else if (n.EndsWith("head")) score -= 500;
                if (score < bestScore)
                {
                    best = bones[i];
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>
        /// POV 目标是主角时的处理（第一人称和自由飞行共用）：
        /// 1) 激活被游戏隐藏的 Actor 模型（否则动画冻结、镜头静止）
        /// 2) 关闭主角近距离遮挡渐隐（PlayerActorControl 的 X-ray 淡出系统，
        ///    相机太近时把主角模型淡出，第一人称贴头/飞行起点在头部附近都会触发）
        /// </summary>
        /// <summary>v2.4.0: 把绝对视角对准人物头部当前朝向（进入/切换 POV 目标、按 R 重置时调用）</summary>
        private static void InitFpvView()
        {
            if (curTarget == null || curTarget.Head == null) return;
            Vector3 f = curTarget.Head.forward.normalized;
            Vector3 flat = Vector3.ProjectOnPlane(f, Vector3.up);
            if (flat.sqrMagnitude < 0.01f)
            {
                flat = Vector3.ProjectOnPlane(curTarget.Unit.trans.forward, Vector3.up);
            }
            if (flat.sqrMagnitude < 0.01f) flat = Vector3.forward;
            yawOff = Mathf.Atan2(flat.x, flat.z) * Mathf.Rad2Deg;
            pitchOff = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -85f, 85f);
        }

        private static void ApplyPlayerVisuals(PlayerControl pc)
        {
            if (pc == null || pc.Actor == null) return;

            if (!pc.Actor.gameObject.activeSelf)
            {
                try
                {
                    pc.Actor.gameObject.SetActive(true);
                    activatedActorPlayer = pc;
                    SLog.LogInfo("POV: 主角模型 Actor 原为隐藏，已激活（退出 POV 时还原）");
                }
                catch (Exception e)
                {
                    SLog.LogInfo("POV: 激活主角 Actor 失败: " + e.Message);
                }
            }

            // 关闭遮挡渐隐：FadeCotoff 归零（Update 里的 lerp 目标变 0），并直接刷材质立即生效
            try
            {
                if (savedFadeCutoff < 0f)
                {
                    savedFadeCutoff = pc.Actor.FadeCotoff;
                    SLog.LogInfo("POV: 关闭主角遮挡渐隐（原 FadeCotoff=" + savedFadeCutoff + "），身体不再被淡出");
                }
                pc.Actor.FadeCotoff = 0f;
                SetActorCutoff(pc.Actor, 0f);
            }
            catch (Exception e)
            {
                SLog.LogInfo("POV: 关闭遮挡渐隐失败: " + e.Message);
            }
        }

        /// <summary>直接把 Actor 所有材质的 _CustomCutoff 写成目标值（立即生效，不等 lerp）</summary>
        private static void SetActorCutoff(PlayerActorControl actor, float value)
        {
            var renderers = actor.GetComponentsInChildren<Renderer>(false);
            for (int i = 0; i < renderers.Length; i++)
            {
                var mats = renderers[i].materials;
                for (int j = 0; j < mats.Length; j++)
                {
                    if (mats[j] != null && mats[j].HasProperty("_CustomCutoff"))
                    {
                        mats[j].SetFloat("_CustomCutoff", value);
                    }
                }
            }
        }

        /// <summary>退出/切换 POV 时还原主角状态（模型隐藏 + 遮挡渐隐强度）</summary>
        private static void RestorePlayerActor()
        {
            // 还原遮挡渐隐强度
            if (savedFadeCutoff >= 0f)
            {
                var fp = PlayerControl.Self;
                if (fp != null && fp.Actor != null)
                {
                    try
                    {
                        fp.Actor.FadeCotoff = savedFadeCutoff;
                        SLog.LogInfo("POV: 主角遮挡渐隐已还原（FadeCotoff=" + savedFadeCutoff + "）");
                    }
                    catch { }
                }
                savedFadeCutoff = -1f;
            }

            var pc = activatedActorPlayer;
            activatedActorPlayer = null;
            if (pc == null) return;
            try
            {
                if (pc.Actor != null && pc.Actor.gameObject.activeSelf && pc.Normal)
                {
                    pc.Actor.gameObject.SetActive(false);
                    SLog.LogInfo("POV: 主角模型已还原为隐藏");
                }
            }
            catch { }
        }

        private static void HideHeadRenderers(Transform head, bool restoreFirst = true)
        {
            if (restoreFirst) RestoreRenderers();
            if (head == null) return;

            currentHeadBone = head;
            originalHeadScale = head.localScale == Vector3.zero ? Vector3.one : head.localScale;
            // 立即压为 0
            head.localScale = Vector3.zero;

            // 兜底：如果模型上有附着在头骨下的独立道具/饰品 Renderer（例如眼镜、帽子），依然将其禁用
            var renderers = head.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i].enabled)
                {
                    renderers[i].enabled = false;
                    hiddenRenderers.Add(renderers[i]);
                }
            }
        }

        private static void RestoreRenderers()
        {
            // 1. 还原头骨缩放
            if (currentHeadBone != null)
            {
                currentHeadBone.localScale = originalHeadScale;
                currentHeadBone = null;
            }

            // 2. 还原禁用的挂件 Renderer
            for (int i = 0; i < hiddenRenderers.Count; i++)
            {
                if (hiddenRenderers[i] != null)
                {
                    hiddenRenderers[i].enabled = true;
                }
            }
            hiddenRenderers.Clear();
        }

        // ================================================================
        // 屏幕绘制：菜单 + 提示（由运行时创建的 PovNoticeGui.OnGUI 调用）
        // ================================================================

        private static void EnsureNoticeGui()
        {
            if (noticeGo != null) return;
            noticeGo = new GameObject("PovCamNotice");
            UnityEngine.Object.DontDestroyOnLoad(noticeGo);
            noticeGo.AddComponent<PovNoticeGui>();
            SLog.LogInfo("[diag] PovNoticeGui 已创建（运行时动态）");
        }

        internal static void DrawGui()
        {
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 24,
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold
                };
                noticeStyle.normal.textColor = Color.white;
                statusStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.MiddleLeft };
                statusStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
                selBtnStyle = new GUIStyle(GUI.skin.button) { fontStyle = FontStyle.Bold, fontSize = 15 };
                selBtnStyle.normal.textColor = new Color(1f, 0.85f, 0.2f);
                selBtnStyle.hover.textColor = new Color(1f, 0.85f, 0.2f);
                menuLabelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
                menuLabelStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);
            }

            // ---- 顶部提示（淡出）----
            if (notice != null && Time.time < noticeUntil)
            {
                float a = Mathf.Clamp01((noticeUntil - Time.time) / 0.5f);
                Color prevColor = GUI.color;
                Color prevBg = GUI.backgroundColor;
                GUI.color = new Color(1f, 1f, 1f, a);
                GUI.backgroundColor = new Color(0f, 0f, 0f, 0.75f);
                var c = noticeStyle.CalcSize(new GUIContent(notice));
                var rect = new Rect((Screen.width - c.x - 30f) / 2f, 60f, c.x + 30f, c.y + 16f);
                GUI.Box(rect, notice, noticeStyle);
                GUI.color = prevColor;
                GUI.backgroundColor = prevBg;
            }

            // ---- 左上角状态角标 ----
            //if (state != PovState.Off || menuOpen)
            //{
            //    string status;
            //    if (state == PovState.Fpv)
            //        status = "[POV 第一人称] " + (curTarget != null ? curTarget.DisplayName : "?")
            //            + "  |  右键自由转向 " + CfgToggle.Value + " 菜单  " + CfgCycle.Value + " 换人  " + CfgReset.Value + " 对准人物  头部隐藏:" + (CfgHideHead.Value ? "开" : "关");
            //    else if (state == PovState.Fly)
            //        status = "[POV 自由飞行]  右键视角 WASD飞行 Shift加速  " + CfgToggle.Value + " 菜单";
            //    else
            //        status = "[PovCam] " + CfgToggle.Value + " 菜单";
            //    Color prevBg2 = GUI.backgroundColor;
            //    GUI.backgroundColor = new Color(0f, 0f, 0f, 0.6f);
            //    GUI.Box(new Rect(10f, 10f, 760f, 30f), status, statusStyle);
            //    GUI.backgroundColor = prevBg2;
            //}

            // ---- 主菜单窗口 ----
            if (!menuOpen) return;

            menuRect = GUILayout.Window(741239, menuRect, (id) =>
            {
                GUILayout.Label("选择人物（点击进入第一人称，或 ↑↓ + Enter）");

                if (targets.Count == 0)
                {
                    GUILayout.Label("  未找到场景人物，先进入有角色的场景再开菜单");
                }
                else
                {
                    menuScroll = GUILayout.BeginScrollView(menuScroll, GUILayout.Height(Mathf.Min(260f, targets.Count * 30f + 8f)));
                    for (int i = 0; i < targets.Count; i++)
                    {
                        bool isCur = state == PovState.Fpv && curTarget != null && curTarget.Unit == targets[i].Unit;
                        string label = (i == selIdx ? "▶ " : "   ") + targets[i].DisplayName
                            + (isCur ? "   [当前]" : "")
                            + (targets[i].Inactive ? "   (隐藏)" : "");
                        GUIStyle st = i == selIdx ? selBtnStyle : GUI.skin.button;
                        if (GUILayout.Button(label, st))
                        {
                            selIdx = i;
                            EnterFpvAt(i);
                        }
                    }
                    GUILayout.EndScrollView();
                }

                GUILayout.Space(8f);

                bool newHide = GUILayout.Toggle(CfgHideHead.Value, "第一人称时隐藏该人物头部（防满屏后脑勺）", GUILayout.Width(380f));
                if (newHide != CfgHideHead.Value)
                {
                    CfgHideHead.Value = newHide;
                    ApplyHideState();
                    ShowNotice("头部隐藏: " + (newHide ? "开" : "关"));
                }

                GUILayout.Space(8f);
                GUILayout.BeginHorizontal();

                if (GUILayout.Button("自由飞行"))
                {
                    EnterFly();
                    CloseMenu();
                }
                if (state != PovState.Off && GUILayout.Button("关闭 POV"))
                {
                    Shutdown();
                }
                if (GUILayout.Button("关闭菜单"))
                {
                    CloseMenu();
                }

                GUILayout.EndHorizontal();

                GUILayout.Space(4f);
                string stat = state == PovState.Fpv ? "当前: " + (curTarget != null ? curTarget.DisplayName : "?")
                    : state == PovState.Fly ? "自由飞行中" : "POV 未开启";
                GUILayout.Label(stat + "    |  Esc 关闭菜单  滚轮调FOV", menuLabelStyle);

                GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
            }, "PovCam 设置");
        }

        private static void ShowNotice(string msg, float duration = 2.5f)
        {
            notice = msg;
            noticeUntil = Time.time + duration;
            if (SLog != null) SLog.LogInfo(msg);
        }
    }

    /// <summary>Harmony 补丁：游戏主管理器每帧 LateUpdate 之后驱动插件逻辑</summary>
    internal static class Patches
    {
        [HarmonyPatch(typeof(GameManagement), "LateUpdate")]
        [HarmonyPostfix]
        private static void GameManagementLateUpdatePostfix()
        {
            PovCamPlugin.Tick();
        }
    }

    /// <summary>运行时创建的绘制组件（此类 OnGUI 已验证可运行）</summary>
    public class PovNoticeGui : MonoBehaviour
    {
        private void OnGUI()
        {
            PovCamPlugin.DrawGui();
        }
    }
}
