// ============================================================
//  UIButtonAPI.cs  —  Multi-mod menu system (callback-based)
//
//  HOW TO USE
//  ──────────
//  1. Register your mod in OnApplicationStart:
//       _handle = UIButtonAPI.UIButtonAPI.RegisterMod("MyMod");
//
//  2. Subscribe to OnUIReady:
//       UIButtonAPI.UIButtonAPI.OnUIReady.AddListener(() => SetupUI());
//
//  3. Add buttons, toggles, input fields — pass callbacks directly:
//       UIButtonAPI.UIButtonAPI.MakeButton(_handle, 0, 0, "Say Hi",
//           () => MelonLogger.Msg("Hi!"));
//
//       var tog = UIButtonAPI.UIButtonAPI.MakeToggle(_handle, 1, 0, "God Mode",
//           isOn => godMode = isOn);
//       UIButtonAPI.UIButtonAPI.SyncToggle(tog, godMode); // sync saved state
//
//  4. Create sub-menus:
//       int sub = UIButtonAPI.UIButtonAPI.CreateSubMenu(_handle, "Settings");
//       UIButtonAPI.UIButtonAPI.MakeButton(_handle, 0, 1, "Settings",
//           () => UIButtonAPI.UIButtonAPI.OpenSubMenu(_handle, sub));
//       UIButtonAPI.UIButtonAPI.MakeButtonInSubMenu(_handle, sub, 0, 0, "Feature",
//           () => DoThing());
//
//  GRID: (0,0)=Top-Left, step=420 units. Negatives allowed.
//  Back button auto-added at (3,2) in every sub-menu.
//  Shortcut buttons stack at (-1,0), (-1,1) etc. or use RegisterMod("Name",x,y).
// ============================================================

using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun;

[assembly: MelonInfo(typeof(UIButtonAPI.UIButtonAPI), "UIButtonAPI", "4.0.0", "Lumarizle + AI")]
[assembly: MelonGame]

namespace UIButtonAPI
{
    // ── ModHandle ──────────────────────────────────────────────────────
    public enum ArrowDir { Right, Left, Up, Down }

    public class ModHandle
    {
        public int ID;
        public string Name;
        public int ShortcutGridX = int.MinValue;
        public int ShortcutGridY = int.MinValue;
        internal GameObject MenuPanel;
        internal List<GameObject> SubMenus = new List<GameObject>();
        internal GameObject ActiveSubMenu = null;
        internal bool IsOpen = false;
        internal List<GameObject> BigPages = new List<GameObject>();
        internal GameObject ActiveBigPage = null;
    }

    public class UIButtonAPI : MelonMod
    {
        // ── Public ────────────────────────────────────────────────────
        /// <summary>Fires once when the menu system is ready.</summary>
        public static UnityEvent OnUIReady = new UnityEvent();
        /// <summary>True once BuildUI has completed. Poll as fallback.</summary>
        public static bool MainMenuReady = false;

        // ── Internal ──────────────────────────────────────────────────
        private static GameObject _localPlayer;
        private static Transform _shortcutMenu;
        private static bool _uiBuilt = false;
        private static List<ModHandle> _handles = new List<ModHandle>();
        private static List<ModHandle> _pendingHandles = new List<ModHandle>();

        // Prefabs
        private static GameObject _btnPrefab;
        private static GameObject _togglePrefab;
        private static GameObject _btnArrowRight, _btnArrowLeft, _btnArrowUp, _btnArrowDown;
        private static GameObject _bigBtnPrefab;
        private static GameObject _bigTogglePrefab;
        private static GameObject _bigPagePrefab;
        private static GameObject _bigCommentPrefab;

        // ── Grid constants ─────────────────────────────────────────────
        private const float GridOriginX = -630f;
        private const float GridOriginY = 1471.6f;
        private const float GridStepX = 420f;
        private const float GridStepY = -420f;
        private const float BigGridOriginX = -255f;
        private const float BigGridOriginY = 70f;
        private const float BigGridStep = 65f;

        // ══════════════════════════════════════════════════════════════
        //  MELONLOADER LIFECYCLE
        // ══════════════════════════════════════════════════════════════

        public override void OnApplicationStart()
        {
            MelonLogger.Msg("UIButtonAPI v4.0 loaded.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _uiBuilt = false;
            MainMenuReady = false;
            _localPlayer = null;
            _shortcutMenu = null;
            _btnPrefab = _togglePrefab = null;
            _bigBtnPrefab = _bigTogglePrefab = _bigPagePrefab = _bigCommentPrefab = null;
            _btnArrowRight = _btnArrowLeft = _btnArrowUp = _btnArrowDown = null;

            foreach (var h in _handles)
            {
                h.MenuPanel = null; h.SubMenus.Clear();
                h.ActiveSubMenu = null; h.IsOpen = false;
                h.BigPages.Clear(); h.ActiveBigPage = null;
            }
            _pendingHandles = new List<ModHandle>(_handles);
            OnUIReady.RemoveAllListeners();
            MelonCoroutines.Start(WaitForLocalPlayer());
        }

        // ══════════════════════════════════════════════════════════════
        //  REGISTRATION
        // ══════════════════════════════════════════════════════════════

        /// <summary>Register your mod. Call in OnApplicationStart. Returns a ModHandle.</summary>
        public static ModHandle RegisterMod(string name) =>
            RegisterMod(name, int.MinValue, int.MinValue);

        /// <summary>Register with a specific shortcut button position instead of auto-stacking.</summary>
        public static ModHandle RegisterMod(string name, int shortcutGridX, int shortcutGridY)
        {
            foreach (var e in _handles) if (e.Name == name) return e;
            var h = new ModHandle { ID = _handles.Count, Name = name, ShortcutGridX = shortcutGridX, ShortcutGridY = shortcutGridY };
            _handles.Add(h);
            _pendingHandles.Add(h);
            return h;
        }

        // ══════════════════════════════════════════════════════════════
        //  PLAYER POLLING
        // ══════════════════════════════════════════════════════════════

        private static System.Collections.IEnumerator WaitForLocalPlayer()
        {
            while (_localPlayer == null)
            {
                foreach (var go in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (!go.name.StartsWith("PhotonDesktopPlayer")) continue;
                    var pv = go.GetComponent<PhotonView>();
                    if (pv != null && pv.IsMine) { _localPlayer = go; break; }
                }
                if (_localPlayer == null) yield return new WaitForSeconds(1f);
            }
            if (!_uiBuilt) BuildUI();
        }

        // ══════════════════════════════════════════════════════════════
        //  BUILD UI
        // ══════════════════════════════════════════════════════════════

        private static void BuildUI()
        {
            Transform quickMenu = _localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM/QuickMenu");
            if (quickMenu == null) { MelonLogger.Warning("UIButtonAPI: QuickMenu not found."); return; }

            _shortcutMenu = _localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM/QuickMenu/ShortcutMenu");

            GameObject menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            _btnPrefab = Resources.Load<GameObject>("OLD_BUTTON");
            _togglePrefab = Resources.Load<GameObject>("OLD_TOGGLE");
            _bigBtnPrefab = Resources.Load<GameObject>("OLD_BIGMENUBUTTON");
            _bigTogglePrefab = Resources.Load<GameObject>("OLD_BIGMENUTOGGLE");
            _bigPagePrefab = Resources.Load<GameObject>("OLD_BIGMENUPAGE");
            _bigCommentPrefab = Resources.Load<GameObject>("OLD_BIGMENUCOMMENT");
            _btnArrowRight = Resources.Load<GameObject>("OLD_BUTTONARROWRIGHT");
            _btnArrowLeft = Resources.Load<GameObject>("OLD_BUTTONARROWLEFT");
            _btnArrowUp = Resources.Load<GameObject>("OLD_BUTTONARROWUP");
            _btnArrowDown = Resources.Load<GameObject>("OLD_BUTTONARROWDOWN");

            if (menuPrefab == null || _btnPrefab == null || _togglePrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: Missing required prefabs.");
                return;
            }

            int autoSlotY = 0;
            foreach (var handle in _pendingHandles)
            {
                handle.MenuPanel = GameObject.Instantiate(menuPrefab, quickMenu);
                handle.MenuPanel.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
                handle.MenuPanel.SetActive(false);
                AddBackButton(handle.MenuPanel, () => CloseMainMenu(handle));

                if (_shortcutMenu != null)
                {
                    int bx = handle.ShortcutGridX != int.MinValue ? handle.ShortcutGridX : -1;
                    int by = handle.ShortcutGridY != int.MinValue ? handle.ShortcutGridY : autoSlotY;
                    if (handle.ShortcutGridX == int.MinValue) autoSlotY++;

                    GameObject btn = GameObject.Instantiate(_btnPrefab, _shortcutMenu);
                    btn.transform.localPosition = GridToUnity(bx, by);
                    SetText(btn, "ButtonText", handle.Name);
                    var h = handle;
                    btn.GetComponent<Button>()?.onClick.AddListener(() => ToggleMainMenu(h));
                }
            }
            _pendingHandles.Clear();
            _uiBuilt = MainMenuReady = true;
            OnUIReady?.Invoke();
        }

        private static void AddBackButton(GameObject panel, System.Action onBack)
        {
            var btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(4, 2);
            SetText(btn, "ButtonText", "Back");
            btn.GetComponent<Button>()?.onClick.AddListener(() => onBack());
        }

        // ══════════════════════════════════════════════════════════════
        //  OPEN / CLOSE
        // ══════════════════════════════════════════════════════════════

        private static void ToggleMainMenu(ModHandle h) { if (h.IsOpen) CloseMainMenu(h); else OpenMainMenu(h); }

        private static void OpenMainMenu(ModHandle h)
        {
            foreach (var x in _handles) if (x != h && x.IsOpen) CloseMainMenu(x);
            if (h.ActiveSubMenu != null) { h.ActiveSubMenu.SetActive(false); h.ActiveSubMenu = null; }
            h.MenuPanel.SetActive(true); h.IsOpen = true;
            if (_shortcutMenu != null) _shortcutMenu.gameObject.SetActive(false);
        }

        private static void CloseMainMenu(ModHandle h)
        {
            if (h.MenuPanel != null) h.MenuPanel.SetActive(false);
            h.IsOpen = false;
            bool anyOpen = false;
            foreach (var x in _handles) if (x.IsOpen) { anyOpen = true; break; }
            if (!anyOpen && _shortcutMenu != null) _shortcutMenu.gameObject.SetActive(true);
        }

        // ══════════════════════════════════════════════════════════════
        //  SUB-MENU SYSTEM
        // ══════════════════════════════════════════════════════════════

        /// <summary>Creates a sub-menu. Returns its index. Back button auto-added at (3,2).</summary>
        public static int CreateSubMenu(ModHandle h, string title) =>
            CreateSubMenuInternal(h, title, () => CloseSubMenu(h));

        /// <summary>Creates a sub-menu with a custom back action.</summary>
        public static int CreateSubMenuInternal(ModHandle h, string title, System.Action backAction)
        {
            if (h?.MenuPanel == null) { MelonLogger.Warning("UIButtonAPI: CreateSubMenu called before UI ready."); return -1; }
            var menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            if (menuPrefab == null) return -1;
            var sub = GameObject.Instantiate(menuPrefab, h.MenuPanel.transform.parent);
            sub.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
            sub.SetActive(false);
            h.SubMenus.Add(sub);
            int id = h.SubMenus.Count - 1;
            AddBackButton(sub, backAction);
            return id;
        }

        /// <summary>Opens a sub-menu, hiding the current panel.</summary>
        public static void OpenSubMenu(ModHandle h, int subID)
        {
            if (h == null || subID < 0 || subID >= h.SubMenus.Count) return;
            if (h.ActiveSubMenu != null) h.ActiveSubMenu.SetActive(false);
            else h.MenuPanel.SetActive(false);
            h.SubMenus[subID].SetActive(true);
            h.ActiveSubMenu = h.SubMenus[subID];
        }

        /// <summary>Closes the active sub-menu, returning to the main menu.</summary>
        public static void CloseSubMenu(ModHandle h)
        {
            if (h?.ActiveSubMenu != null) { h.ActiveSubMenu.SetActive(false); h.ActiveSubMenu = null; }
            h?.MenuPanel.SetActive(true);
        }

        /// <summary>Returns the raw sub-menu GameObject.</summary>
        public static GameObject GetSubMenu(ModHandle h, int subID)
        {
            if (h == null || subID < 0 || subID >= h.SubMenus.Count) return null;
            return h.SubMenus[subID];
        }

        // ══════════════════════════════════════════════════════════════
        //  PUBLIC API — QUICK MENU
        // ══════════════════════════════════════════════════════════════

        /// <summary>Converts grid (col,row) to Unity local position.</summary>
        public static Vector3 GridToUnity(int col, int row) =>
            new Vector3(GridOriginX + col * GridStepX, GridOriginY + row * GridStepY, 0f);

        /// <summary>Adds a button to the mod's main menu.</summary>
        public static GameObject MakeButton(ModHandle h, int col, int row, string text, System.Action onClick)
        {
            if (h?.MenuPanel == null) { MelonLogger.Warning("UIButtonAPI: MakeButton called before UI ready."); return null; }
            return SpawnButton(h.MenuPanel, col, row, text, onClick);
        }

        /// <summary>Adds a toggle to the mod's main menu. Returns the GO so you can call SyncToggle on it.</summary>
        public static GameObject MakeToggle(ModHandle h, int col, int row, string text, System.Action<bool> onChange, bool DefState = false)
        {
            if (h?.MenuPanel == null) { MelonLogger.Warning("UIButtonAPI: MakeToggle called before UI ready."); return null; }
            return SpawnToggle(h.MenuPanel, col, row, text, onChange, DefState);
        }

        /// <summary>Adds a button inside a sub-menu.</summary>
        public static GameObject MakeButtonInSubMenu(ModHandle h, int subID, int col, int row, string text, System.Action onClick)
        {
            var sub = GetSubMenu(h, subID);
            if (sub == null) { MelonLogger.Warning("UIButtonAPI: MakeButtonInSubMenu — invalid subID."); return null; }
            return SpawnButton(sub, col, row, text, onClick);
        }

        /// <summary>Adds a toggle inside a sub-menu.</summary>
        public static GameObject MakeToggleInSubMenu(ModHandle h, int subID, int col, int row, string text, System.Action<bool> onChange, bool DefState = false)
        {
            var sub = GetSubMenu(h, subID);
            if (sub == null) { MelonLogger.Warning("UIButtonAPI: MakeToggleInSubMenu — invalid subID."); return null; }
            return SpawnToggle(sub, col, row, text, onChange, DefState);
        }

        /// <summary>Adds an input field button. Opens the game's Input Popup on click.</summary>
        public static GameObject MakeInputField(ModHandle h, GameObject localPlayer, int subID, int col, int row,
            string label, System.Action<string> onSubmit, System.Func<string> defaultValue = null)
        {
            var panel = subID < 0 ? h?.MenuPanel : GetSubMenu(h, subID);
            if (panel == null) { MelonLogger.Warning("UIButtonAPI: MakeInputField — invalid panel."); return null; }
            var btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(col, row);
            SetText(btn, "ButtonText", label);
            btn.GetComponent<Button>()?.onClick.AddListener(() =>
            {
                string def = defaultValue != null ? defaultValue() : "";
                OpenInputPopup(localPlayer, label, onSubmit, def);
            });
            return btn;
        }


        /// <summary>
        /// Spawns an arrow button (no text) on any panel or sub-menu GameObject.
        /// Get the panel via h.MenuPanel or GetSubMenu(h, subID).
        ///
        /// Example:
        ///   var panel = UIButtonAPI.UIButtonAPI.GetSubMenu(_h, _subPlayerList);
        ///   UIButtonAPI.UIButtonAPI.MakeArrowButton(panel, 0, 2, ArrowDir.Left, () => PrevPage());
        /// </summary>
        public static GameObject MakeArrowButton(GameObject panel, int col, int row, ArrowDir dir, System.Action onClick)
        {
            if (panel == null) { MelonLogger.Warning("UIButtonAPI: MakeArrowButton — null panel."); return null; }
            return SpawnArrow(panel, col, row, dir, onClick);
        }

        /// <summary>Sets a toggle's visual isOn state without firing its onChange callback.</summary>
        public static void SyncToggle(GameObject toggleGO, bool value)
        {
            if (toggleGO == null) return;
            var tog = toggleGO.GetComponent<Toggle>();
            tog?.SetIsOnWithoutNotify(value);
        }

        // ══════════════════════════════════════════════════════════════
        //  INPUT POPUP
        // ══════════════════════════════════════════════════════════════

        public static void OpenInputPopup(GameObject localPlayer, string title,
            System.Action<string> onSubmit, string defaultValue = "")
        {
            if (localPlayer == null) { MelonLogger.Warning("UIButtonAPI: OpenInputPopup — no local player."); return; }

            Transform popupRoot = localPlayer.transform.Find(
                "Camera Offset/UI/Menu_Expanded/Input Popup (1)");
            if (popupRoot == null) { MelonLogger.Warning("UIButtonAPI: Input Popup (1) not found."); return; }

            // Hide QM
            Transform qm = localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM");
            if (qm != null) qm.gameObject.SetActive(false);

            popupRoot.gameObject.SetActive(true);
            popupRoot.localScale = new Vector3(1.84f, 1.84f, 1.84f);

            Transform titleT = popupRoot.Find("InputPopup/TitleText");
            if (titleT != null) { var t = titleT.GetComponent<Text>(); if (t != null) t.text = title; }

            Transform inputT = popupRoot.Find("InputPopup/InputField");
            InputField inputField = inputT?.GetComponent<InputField>();
            if (inputField != null) inputField.text = defaultValue;

            Transform submitT = popupRoot.Find("InputPopup/ButtonCenter");
            Button submitBtn = submitT?.GetComponent<Button>();
            if (submitBtn != null)
            {
                submitBtn.onClick.RemoveAllListeners();
                submitBtn.onClick.AddListener(() =>
                {
                    string value = inputField != null ? inputField.text : "";
                    onSubmit?.Invoke(value);
                    if (qm != null) qm.gameObject.SetActive(true);
                    bool faded = false;
                    foreach (var mb in popupRoot.GetComponents<MonoBehaviour>())
                    {
                        var method = mb.GetType().GetMethod("FadeOutAndDisable");
                        if (method != null) { method.Invoke(mb, null); faded = true; break; }
                    }
                    if (!faded) popupRoot.gameObject.SetActive(false);
                });
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  BIG MENU API
        // ══════════════════════════════════════════════════════════════

        public static Vector3 BigGridToUnity(int col, int row) =>
            new Vector3(BigGridOriginX + col * BigGridStep, BigGridOriginY - row * BigGridStep, 0f);

        /// <summary>Creates a new page in Menu_Expanded. Returns its pageID.</summary>
        public static int CreateBigPage(ModHandle h, string title)
        {
            if (h == null || _localPlayer == null || _bigPagePrefab == null) { MelonLogger.Warning("UIButtonAPI: CreateBigPage failed."); return -1; }
            Transform menuExpanded = _localPlayer.transform.Find("Camera Offset/UI/Menu_Expanded");
            if (menuExpanded == null) { MelonLogger.Warning("UIButtonAPI: Menu_Expanded not found."); return -1; }
            var page = GameObject.Instantiate(_bigPagePrefab, menuExpanded);
            page.transform.localPosition = new Vector3(0.40077f, 4.253306f, 0f);
            page.transform.localScale = new Vector3(2.501825f, 2.501825f, 2.501825f);
            page.SetActive(false);
            Transform titleT = page.transform.Find("TitlePanel/TitleText");
            if (titleT != null) { var tmp = titleT.GetComponent<TMPro.TextMeshProUGUI>(); if (tmp != null) tmp.text = title; }
            h.BigPages.Add(page);
            return h.BigPages.Count - 1;
        }

        /// <summary>Creates a sub-big-page with an auto Back button at (8,0) returning to parentPageID.</summary>
        public static int CreateSubBigPage(ModHandle h, string title, int parentPageID)
        {
            int id = CreateBigPage(h, title);
            if (id < 0 || _bigBtnPrefab == null) return id;
            var page = h.BigPages[id];
            var backBtn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            backBtn.transform.localPosition = BigGridToUnity(8, 0);
            SetTMP(backBtn, "ButtonText", "< Back");
            backBtn.GetComponent<Button>()?.onClick.AddListener(() => ShowBigPage(h, parentPageID));
            return id;
        }

        /// <summary>Shows a big page, hiding all others for this mod. Hides QM.</summary>
        public static void ShowBigPage(ModHandle h, int pageID)
        {
            if (h == null || pageID < 0 || pageID >= h.BigPages.Count) return;
            foreach (var p in h.BigPages) p.SetActive(false);
            h.BigPages[pageID].SetActive(true);
            h.ActiveBigPage = h.BigPages[pageID];
        }

        /// <summary>Opens a sub-page, hiding the parent.</summary>
        public static void OpenSubBigPage(ModHandle h, int parentPageID, int subPageID)
        {
            if (h == null) return;
            if (parentPageID >= 0 && parentPageID < h.BigPages.Count) h.BigPages[parentPageID].SetActive(false);
            if (subPageID >= 0 && subPageID < h.BigPages.Count) { h.BigPages[subPageID].SetActive(true); h.ActiveBigPage = h.BigPages[subPageID]; }
        }

        /// <summary>Hides all big pages for this mod.</summary>
        public static void HideAllBigPages(ModHandle h) { if (h == null) return; foreach (var p in h.BigPages) p.SetActive(false); h.ActiveBigPage = null; }

        /// <summary>Closes all big pages and restores the main menu.</summary>
        public static void CloseBigMenu(ModHandle h) => HideAllBigPages(h);

        public static GameObject GetBigPage(ModHandle h, int pageID)
        {
            if (h == null || pageID < 0 || pageID >= h.BigPages.Count) return null;
            return h.BigPages[pageID];
        }

        /// <summary>Adds a button to a big page.</summary>
        public static GameObject MakeBigButton(ModHandle h, int pageID, int col, int row, string text, System.Action onClick)
        {
            var page = GetBigPage(h, pageID);
            if (page == null || _bigBtnPrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigButton failed."); return null; }
            return SpawnBigButton(page, col, row, text, onClick);
        }

        /// <summary>Adds a toggle to a big page. Returns the GO for SyncToggle.</summary>
        public static GameObject MakeBigToggle(ModHandle h, int pageID, int col, int row, string text, System.Action<bool> onChange, bool Defstate = false)
        {
            var page = GetBigPage(h, pageID);
            if (page == null || _bigTogglePrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigToggle failed."); return null; }
            return SpawnBigToggle(page, col, row, text, onChange, Defstate);
        }

        /// <summary>Adds a comment label to a big page.</summary>
        public static GameObject MakeBigComment(ModHandle h, int pageID, int col, int row, string text)
        {
            var page = GetBigPage(h, pageID);
            if (page == null || _bigCommentPrefab == null) return null;
            var obj = GameObject.Instantiate(_bigCommentPrefab, page.transform);
            obj.transform.localPosition = BigGridToUnity(col, row);
            var tmp = obj.GetComponent<TMPro.TextMeshProUGUI>() ?? obj.GetComponentInChildren<TMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = text;
            return obj;
        }

        /// <summary>Adds an input field button to a big page.</summary>
        public static GameObject MakeBigInputField(ModHandle h, GameObject localPlayer, int pageID, int col, int row,
            string label, System.Action<string> onSubmit, System.Func<string> defaultValue = null)
        {
            var page = GetBigPage(h, pageID);
            if (page == null || _bigBtnPrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigInputField failed."); return null; }
            var btn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            btn.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(btn, "ButtonText", label);
            btn.GetComponent<Button>()?.onClick.AddListener(() =>
            {
                string def = defaultValue != null ? defaultValue() : "";
                OpenInputPopup(localPlayer, label, onSubmit, def);
            });
            return btn;
        }

        // ══════════════════════════════════════════════════════════════
        //  INTERNAL HELPERS
        // ══════════════════════════════════════════════════════════════

        private static GameObject SpawnArrow(GameObject panel, int col, int row, ArrowDir dir, System.Action onClick)
        {
            GameObject prefab;
            switch (dir)
            {
                case ArrowDir.Left: prefab = _btnArrowLeft; break;
                case ArrowDir.Up: prefab = _btnArrowUp; break;
                case ArrowDir.Down: prefab = _btnArrowDown; break;
                default: prefab = _btnArrowRight; break;
            }
            if (prefab == null) { MelonLogger.Warning($"UIButtonAPI: Arrow prefab for {dir} not loaded."); return null; }
            var btn = GameObject.Instantiate(prefab, panel.transform);
            btn.transform.localPosition = GridToUnity(col, row);
            if (onClick != null) btn.GetComponent<Button>()?.onClick.AddListener(() => onClick());
            return btn;
        }

        private static GameObject SpawnButton(GameObject panel, int col, int row, string text, System.Action onClick)
        {
            var btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(col, row);
            SetText(btn, "ButtonText", text);
            if (onClick != null) btn.GetComponent<Button>()?.onClick.AddListener(() => onClick());
            return btn;
        }

        private static GameObject SpawnToggle(GameObject panel, int col, int row, string text, System.Action<bool> onChange, bool defState = false)
        {
            var obj = GameObject.Instantiate(_togglePrefab, panel.transform);
            obj.transform.localPosition = GridToUnity(col, row);
            SetText(obj, "ButtonText", text);
            var comp = obj.GetComponent<Toggle>();
            if (comp != null) { comp.isOn = defState; if (onChange != null) comp.onValueChanged.AddListener(isOn => onChange(isOn)); }
            return obj;
        }

        private static GameObject SpawnBigButton(GameObject page, int col, int row, string text, System.Action onClick)
        {
            var btn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            btn.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(btn, "ButtonText", text);
            if (onClick != null) btn.GetComponent<Button>()?.onClick.AddListener(() => onClick());
            return btn;
        }

        private static GameObject SpawnBigToggle(GameObject page, int col, int row, string text, System.Action<bool> onChange, bool defState = false)
        {
            var obj = GameObject.Instantiate(_bigTogglePrefab, page.transform);
            obj.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(obj, "ButtonText", text);
            var comp = obj.GetComponent<Toggle>();
            if (comp != null) { comp.isOn = defState; if (onChange != null) comp.onValueChanged.AddListener(isOn => onChange(isOn)); }
            return obj;
        }

        private static void SetText(GameObject parent, string childName, string text)
        {
            var t = parent.transform.Find(childName);
            if (t == null) return;
            var leg = t.GetComponent<Text>();
            if (leg != null) leg.text = text;
        }

        private static void SetTMP(GameObject parent, string childName, string text)
        {
            var t = parent.transform.Find(childName);
            if (t == null) return;
            var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = text;
        }
    }
}