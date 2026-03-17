// ============================================================
//  UIButtonAPI.cs  —  Multi-mod menu system
//
//  HOW TO USE
//  ──────────
//  1. In OnApplicationStart, register your mod:
//       var handle = UIButtonAPI.UIButtonAPI.RegisterMod("MyMod");
//
//  2. Subscribe to OnUIReady:
//       UIButtonAPI.UIButtonAPI.OnUIReady.AddListener(() => SetupUI(handle));
//
//  3. Add buttons and toggles using the handle:
//       UIButtonAPI.UIButtonAPI.MakeButton(handle, 0, 0, "Say Hi", 1);
//
//  4. Create sub-menus:
//       int sub = UIButtonAPI.UIButtonAPI.CreateSubMenu(handle, "Settings");
//       UIButtonAPI.UIButtonAPI.MakeButton(handle, 0, 1, "Settings", 2);
//       UIButtonAPI.UIButtonAPI.OnUIButtonClick[2] += () => UIButtonAPI.UIButtonAPI.OpenSubMenu(handle, sub);
//       UIButtonAPI.UIButtonAPI.MakeButtonInSubMenu(handle, sub, 0, 0, "Feature", 3);
//
//  GRID: (0,0)=Top-Left, (3,2)=Bottom-Right, 420 units/cell, negatives allowed.
//  Back button auto-added at (3,2) in every sub-menu.
//  Shortcut buttons stack vertically in ShortcutMenu at (-1, 0), (-1, 1), etc.
// ============================================================

using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun;

[assembly: MelonInfo(typeof(UIButtonAPI.UIButtonAPI), "UIButtonAPI", "3.0.0", "Lumarizle + AI")]
[assembly: MelonGame]

namespace UIButtonAPI
{
    // ── ModHandle ──────────────────────────────────────────────────────
    // Returned by RegisterMod(). Pass it to every API call.
    // Each handle owns its own menu panel, sub-menus, and shortcut button.
    public class ModHandle
    {
        public int ID;           // unique index assigned at registration
        public string Name;         // display name shown on shortcut button
        public int IdOffset;     // auto ID offset — all button/toggle IDs are shifted by this amount

        // Shortcut button position.
        // If ShortcutGridX is int.MinValue the button is auto-stacked vertically at (-1, ID).
        // Set these via RegisterMod() overload or manually before the scene loads.
        public int ShortcutGridX = int.MinValue;
        public int ShortcutGridY = int.MinValue;

        internal GameObject MenuPanel;
        internal List<GameObject> SubMenus = new List<GameObject>();
        internal GameObject ActiveSubMenu = null;
        internal bool IsOpen = false;

        // BigMenu pages owned by this mod
        internal List<GameObject> BigPages = new List<GameObject>();
        internal GameObject ActiveBigPage = null;
    }

    public class UIButtonAPI : MelonMod
    {
        // ── Public Events ──────────────────────────────────────────────
        /// <summary>Fires once when the menu system is ready. Subscribe in OnApplicationStart or OnSceneWasLoaded.</summary>
        public static UnityEvent OnUIReady = new UnityEvent();

        /// <summary>Fires when any button is clicked. Key = buttonID.</summary>
        public static Dictionary<int, UnityEvent> OnUIButtonClick = new Dictionary<int, UnityEvent>();

        /// <summary>Fires when any toggle changes. Key = toggleID. Value = new bool state.</summary>
        public static Dictionary<int, UnityEvent<bool>> OnUIToggleChange = new Dictionary<int, UnityEvent<bool>>();

        /// <summary>Fires when an input field is submitted. Key = inputID. Value = the submitted string.</summary>
        public static Dictionary<int, UnityEvent<string>> OnUIInputSubmit = new Dictionary<int, UnityEvent<string>>();

        // Callbacks that return the current value to pre-fill the input box when opened
        private static Dictionary<int, System.Func<string>> _inputDefaults = new Dictionary<int, System.Func<string>>();

        // Maps toggleID -> Toggle component so we can sync visual state after config load
        private static Dictionary<int, Toggle> _toggleComponents = new Dictionary<int, Toggle>();

        /// <summary>True once the menu system has finished building. Poll this as a fallback.</summary>
        public static bool MainMenuReady = false;

        // ── Internal State ─────────────────────────────────────────────
        private static GameObject _localPlayer;
        private static Transform _shortcutMenu;
        private static bool _uiBuilt = false;

        // All registered mods in registration order
        private static List<ModHandle> _handles = new List<ModHandle>();

        // Pending registrations made before UI was built — processed in BuildUI
        private static List<ModHandle> _pendingHandles = new List<ModHandle>();

        // Prefabs — QuickMenu
        private static GameObject _btnPrefab;
        private static GameObject _togglePrefab;

        // Prefabs — BigMenu
        private static GameObject _bigBtnPrefab;
        private static GameObject _bigTogglePrefab;
        private static GameObject _bigPagePrefab;
        private static GameObject _bigCommentPrefab;

        // ── QuickMenu Grid constants ───────────────────────────────────
        private const float GridOriginX = -630f;
        private const float GridOriginY = 1471.6f;
        private const float GridStepX = 420f;
        private const float GridStepY = -420f;

        // ── BigMenu Grid constants ─────────────────────────────────────
        // (0,0) = top-left = (-255, 70),  step = 65 on both axes
        private const float BigGridOriginX = -255f;
        private const float BigGridOriginY = 70f;
        private const float BigGridStep = 65f;

        // ──────────────────────────────────────────────────────────────
        #region MelonLoader Lifecycle

        public override void OnApplicationStart()
        {
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _uiBuilt = false;
            MainMenuReady = false;
            _localPlayer = null;
            _shortcutMenu = null;
            _btnPrefab = null;
            _togglePrefab = null;
            _bigBtnPrefab = null;
            _bigTogglePrefab = null;
            _bigPagePrefab = null;
            _bigCommentPrefab = null;

            // Reset all handle state but keep the handle registrations themselves
            foreach (var h in _handles)
            {
                h.MenuPanel = null;
                h.SubMenus.Clear();
                h.ActiveSubMenu = null;
                h.IsOpen = false;
                h.BigPages.Clear();
                h.ActiveBigPage = null;
            }

            // Move all handles back to pending so BuildUI re-creates their panels
            _pendingHandles = new List<ModHandle>(_handles);

            OnUIReady.RemoveAllListeners();
            OnUIButtonClick.Clear();
            OnUIToggleChange.Clear();
            OnUIInputSubmit.Clear();
            _inputDefaults.Clear();
            _toggleComponents.Clear();

            MelonCoroutines.Start(WaitForLocalPlayer());
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Registration

        /// <summary>
        /// Register your mod with the menu system. Call this in OnApplicationStart.
        /// Returns a ModHandle you must pass to every API call.
        /// Safe to call before the scene loads — handle is queued and processed when ready.
        ///
        /// Example:
        ///   private static ModHandle _handle;
        ///   public override void OnApplicationStart() {
        ///       _handle = UIButtonAPI.UIButtonAPI.RegisterMod("MyMod");
        ///   }
        /// </summary>
        /// <summary>
        /// Register your mod. Shortcut button auto-stacks at (-1, slot) by default.
        /// Call in OnApplicationStart.
        /// </summary>
        public static ModHandle RegisterMod(string name)
        {
            return RegisterMod(name, int.MinValue, int.MinValue);
        }

        /// <summary>
        /// Register your mod with a specific shortcut button grid position.
        /// Use this to place the button anywhere in ShortcutMenu instead of auto-stacking.
        ///
        /// Example — place at (-1, 2) manually:
        ///   _handle = UIButtonAPI.UIButtonAPI.RegisterMod("MyMod", -1, 2);
        /// </summary>
        public static ModHandle RegisterMod(string name, int shortcutGridX, int shortcutGridY)
        {
            // Re-registration returns existing handle
            foreach (var existing in _handles)
                if (existing.Name == name) return existing;

            var handle = new ModHandle
            {
                ID = _handles.Count,
                Name = name,
                ShortcutGridX = shortcutGridX,
                ShortcutGridY = shortcutGridY,
                IdOffset = _handles.Count * 10000,
            };
            _handles.Add(handle);
            _pendingHandles.Add(handle);
            return handle;
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Player Polling

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

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Build UI

        private static void BuildUI()
        {
            Transform quickMenu = _localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM/QuickMenu");
            if (quickMenu == null) { MelonLogger.Warning("UIButtonAPI: QuickMenu not found."); return; }

            _shortcutMenu = _localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM/QuickMenu/ShortcutMenu");
            if (_shortcutMenu == null) MelonLogger.Warning("UIButtonAPI: ShortcutMenu not found.");

            GameObject menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            _btnPrefab = Resources.Load<GameObject>("OLD_BUTTON");
            _togglePrefab = Resources.Load<GameObject>("OLD_TOGGLE");
            _bigBtnPrefab = Resources.Load<GameObject>("OLD_BIGMENUBUTTON");
            _bigTogglePrefab = Resources.Load<GameObject>("OLD_BIGMENUTOGGLE");
            _bigPagePrefab = Resources.Load<GameObject>("OLD_BIGMENUPAGE");
            _bigCommentPrefab = Resources.Load<GameObject>("OLD_BIGMENUCOMMENT");

            if (menuPrefab == null || _btnPrefab == null || _togglePrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: Missing QM prefabs — menu={menuPrefab != null} btn={_btnPrefab != null} toggle={_togglePrefab != null}");
                return;
            }
            if (_bigBtnPrefab == null || _bigTogglePrefab == null || _bigPagePrefab == null || _bigCommentPrefab == null)
                MelonLogger.Warning("UIButtonAPI: Some BigMenu prefabs are missing — BigMenu API may not work.");

            // Build a menu panel + shortcut button for every registered mod
            int autoSlotY = 0; // counter for auto-stacked mods
            foreach (var handle in _pendingHandles)
            {
                // Menu panel — positioned where ShortcutMenu sits, hidden by default
                handle.MenuPanel = GameObject.Instantiate(menuPrefab, quickMenu);
                handle.MenuPanel.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
                handle.MenuPanel.SetActive(false);

                // Auto Back button at (3,2) for main panel
                AddBackButton(handle.MenuPanel, () => CloseMainMenu(handle));

                // Shortcut button — use custom position if set, otherwise auto-stack at (-1, slot)
                if (_shortcutMenu != null)
                {
                    int bx, by;
                    if (handle.ShortcutGridX != int.MinValue)
                    {
                        // Custom position specified by the mod
                        bx = handle.ShortcutGridX;
                        by = handle.ShortcutGridY;
                    }
                    else
                    {
                        // Auto-stack vertically at (-1, 0), (-1, 1), (-1, 2) ...
                        bx = -1;
                        by = autoSlotY;
                        autoSlotY++;
                    }

                    GameObject btn = GameObject.Instantiate(_btnPrefab, _shortcutMenu);
                    btn.transform.localPosition = GridToUnity(bx, by);
                    SetText(btn, "ButtonText", handle.Name);
                    var comp = btn.GetComponent<Button>();
                    if (comp != null)
                    {
                        ModHandle h = handle;
                        comp.onClick.AddListener(() => ToggleMainMenu(h));
                    }

                }
            }

            _pendingHandles.Clear();
            _uiBuilt = true;
            MainMenuReady = true;
            OnUIReady?.Invoke();
        }

        private static void AddBackButton(GameObject panel, System.Action onBack)
        {
            GameObject btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(3, 2);
            SetText(btn, "ButtonText", "Back");
            btn.GetComponent<Button>()?.onClick.AddListener(() => onBack());
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Open / Close

        private static void ToggleMainMenu(ModHandle handle)
        {
            if (handle.IsOpen) CloseMainMenu(handle);
            else OpenMainMenu(handle);
        }

        private static void OpenMainMenu(ModHandle handle)
        {
            // Close all other mods' menus first
            foreach (var h in _handles)
                if (h != handle && h.IsOpen) CloseMainMenu(h);

            if (handle.ActiveSubMenu != null)
            {
                handle.ActiveSubMenu.SetActive(false);
                handle.ActiveSubMenu = null;
            }

            handle.MenuPanel.SetActive(true);
            handle.IsOpen = true;

            if (_shortcutMenu != null) _shortcutMenu.gameObject.SetActive(false);
        }

        private static void CloseMainMenu(ModHandle handle)
        {
            if (handle.MenuPanel != null) handle.MenuPanel.SetActive(false);
            handle.IsOpen = false;

            // Only restore ShortcutMenu if no other mod is open
            bool anyOpen = false;
            foreach (var h in _handles) if (h.IsOpen) { anyOpen = true; break; }
            if (!anyOpen && _shortcutMenu != null) _shortcutMenu.gameObject.SetActive(true);

        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Sub-Menu System

        /// <summary>
        /// Creates a sub-menu for a mod. Returns an int ID to pass to OpenSubMenu/MakeButtonInSubMenu etc.
        /// A Back button (returns to main menu) is auto-added at (3,2).
        /// </summary>
        public static int CreateSubMenu(ModHandle handle, string title)
        {
            return CreateSubMenuInternal(handle, title, () => CloseSubMenu(handle));
        }

        /// <summary>Creates a sub-menu with a custom Back action (e.g. return to another sub-menu).</summary>
        public static int CreateSubMenuInternal(ModHandle handle, string title, System.Action backAction)
        {
            if (handle?.MenuPanel == null)
            {
                MelonLogger.Warning($"UIButtonAPI: CreateSubMenu called before UI ready for '{handle?.Name}'.");
                return -1;
            }

            GameObject menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            if (menuPrefab == null) return -1;

            GameObject sub = GameObject.Instantiate(menuPrefab, handle.MenuPanel.transform.parent);
            sub.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
            sub.SetActive(false);

            handle.SubMenus.Add(sub);
            int id = handle.SubMenus.Count - 1;

            AddBackButton(sub, backAction);
            return id;
        }

        /// <summary>Opens a sub-menu, hiding the current panel.</summary>
        public static void OpenSubMenu(ModHandle handle, int subMenuID)
        {
            if (handle == null || subMenuID < 0 || subMenuID >= handle.SubMenus.Count) return;
            var sub = handle.SubMenus[subMenuID];

            if (handle.ActiveSubMenu != null) handle.ActiveSubMenu.SetActive(false);
            else handle.MenuPanel.SetActive(false);

            sub.SetActive(true);
            handle.ActiveSubMenu = sub;
        }

        /// <summary>Closes active sub-menu and returns to the mod's main menu.</summary>
        public static void CloseSubMenu(ModHandle handle)
        {
            if (handle?.ActiveSubMenu != null) { handle.ActiveSubMenu.SetActive(false); handle.ActiveSubMenu = null; }
            handle?.MenuPanel.SetActive(true);
        }

        /// <summary>Returns the raw sub-menu GameObject for a given subID.</summary>
        public static GameObject GetSubMenu(ModHandle handle, int subMenuID)
        {
            if (handle == null || subMenuID < 0 || subMenuID >= handle.SubMenus.Count) return null;
            return handle.SubMenus[subMenuID];
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Public API

        /// <summary>Converts grid (col, row) to Unity local position. (0,0)=Top-Left.</summary>
        public static Vector3 GridToUnity(int gridX, int gridY) =>
            new Vector3(GridOriginX + gridX * GridStepX, GridOriginY + gridY * GridStepY, 0f);

        /// <summary>Adds a button to the mod's MAIN menu.</summary>
        public static GameObject MakeButton(ModHandle handle, int gridX, int gridY, string text, int buttonID)
        {
            if (handle?.MenuPanel == null) { MelonLogger.Warning($"UIButtonAPI: MakeButton called before UI ready."); return null; }
            return SpawnButton(handle.MenuPanel, gridX, gridY, text, buttonID, handle.IdOffset);
        }

        /// <summary>Adds a toggle to the mod's MAIN menu.</summary>
        public static GameObject MakeToggle(ModHandle handle, int gridX, int gridY, string text, int toggleID)
        {
            if (handle?.MenuPanel == null) { MelonLogger.Warning($"UIButtonAPI: MakeToggle called before UI ready."); return null; }
            return SpawnToggle(handle.MenuPanel, gridX, gridY, text, toggleID, handle.IdOffset);
        }

        /// <summary>Adds a button inside a sub-menu.</summary>
        public static GameObject MakeButtonInSubMenu(ModHandle handle, int subMenuID, int gridX, int gridY, string text, int buttonID)
        {
            var sub = GetSubMenu(handle, subMenuID);
            if (sub == null) { MelonLogger.Warning($"UIButtonAPI: MakeButtonInSubMenu — invalid subID {subMenuID}."); return null; }
            return SpawnButton(sub, gridX, gridY, text, buttonID);
        }


        /// <summary>
        /// Creates a button that opens the game's built-in Input Popup when clicked.
        /// When the user submits, OnUIInputSubmit[inputID] fires with the entered string.
        /// Pass subMenuID = -1 to place it on the mod's main menu.
        ///
        /// Example:
        ///   UIButtonAPI.UIButtonAPI.MakeInputField(handle, localPlayer, _subMovement, 0, 3, "Set Speed", 800);
        ///   UIButtonAPI.UIButtonAPI.OnUIInputSubmit[800] += val => { if (float.TryParse(val, out float f)) speed = f; };
        /// </summary>
        public static GameObject MakeInputField(ModHandle handle, GameObject localPlayer,
            int subMenuID, int gridX, int gridY, string label, int inputID)
        {
            var panel = subMenuID < 0 ? handle?.MenuPanel : GetSubMenu(handle, subMenuID);
            if (panel == null) { MelonLogger.Warning("UIButtonAPI: MakeInputField — invalid panel."); return null; }

            if (!OnUIInputSubmit.ContainsKey(inputID))
                OnUIInputSubmit[inputID] = new UnityEvent<string>();

            // Spawn a regular button — we'll override its click handler
            if (!OnUIButtonClick.ContainsKey(-inputID)) OnUIButtonClick[-inputID] = new UnityEvent();
            GameObject btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(gridX, gridY);
            SetText(btn, "ButtonText", label);

            var btnComp = btn.GetComponent<Button>();
            if (btnComp != null)
            {
                int id = inputID;
                string lbl = label;
                // defaultValueGetter is set after creation via SetInputDefault — null = empty
                btnComp.onClick.AddListener(() =>
                {
                    string def = _inputDefaults.ContainsKey(id) ? _inputDefaults[id]() : "";
                    OpenInputPopup(localPlayer, lbl, id, def);
                });
            }

            return btn;
        }

        /// <summary>
        /// Opens the game's Input Popup, sets its title, wires submit to fire OnUIInputSubmit[inputID],
        /// and closes via FadeOutAndDisable when submitted.
        /// </summary>
        public static void OpenInputPopup(GameObject localPlayer, string title, int inputID, string defaultValue = "")
        {
            if (localPlayer == null) { MelonLogger.Warning("UIButtonAPI: OpenInputPopup — no local player."); return; }

            Transform popupRoot = localPlayer.transform.Find(
                "Camera Offset/UI/Menu_Expanded/Input Popup (1)");
            if (popupRoot == null) { MelonLogger.Warning("UIButtonAPI: 'Input Popup (1)' not found."); return; }

            // Hide QM so it doesn't overlap the popup
            Transform qm = localPlayer.transform.Find("Camera Offset/UI/Menu_Small/QM");
            if (qm != null) qm.gameObject.SetActive(false);

            popupRoot.gameObject.SetActive(true);

            // Scale the popup to 1.84 on all axes
            popupRoot.localScale = new Vector3(1.84f, 1.84f, 1.84f);

            // Set title
            Transform titleT = popupRoot.Find("InputPopup/TitleText");
            if (titleT != null)
            {
                var txt = titleT.GetComponent<Text>();
                if (txt != null) txt.text = title;
            }

            // Pre-fill InputField with the default/current value
            Transform inputT = popupRoot.Find("InputPopup/InputField");
            InputField inputField = inputT?.GetComponent<InputField>();
            if (inputField != null) inputField.text = defaultValue;

            // Wire submit button — clear previous listeners to avoid stacking across calls
            Transform submitT = popupRoot.Find("InputPopup/ButtonCenter");
            Button submitBtn = submitT?.GetComponent<Button>();
            if (submitBtn != null)
            {
                submitBtn.onClick.RemoveAllListeners();
                int id = inputID;
                submitBtn.onClick.AddListener(() =>
                {
                    string value = inputField != null ? inputField.text : "";
                    if (OnUIInputSubmit.ContainsKey(id)) OnUIInputSubmit[id]?.Invoke(value);

                    // Restore QM
                    if (qm != null) qm.gameObject.SetActive(true);

                    // Close via FadeOutAndDisable if the component exists, else just disable
                    bool faded = false;
                    foreach (var mb in popupRoot.GetComponents<MonoBehaviour>())
                    {
                        var method = mb.GetType().GetMethod("FadeOutAndDisable");
                        if (method != null) { method.Invoke(mb, null); faded = true; break; }
                    }
                    if (!faded) popupRoot.gameObject.SetActive(false);
                });
            }
            else MelonLogger.Warning("UIButtonAPI: ButtonCenter not found on Input Popup.");
        }

        /// <summary>
        /// Registers a callback that returns the current value to pre-fill the input box
        /// when it is opened. Call this right after MakeInputField.
        ///
        /// Example:
        ///   UIButtonAPI.UIButtonAPI.SetInputDefault(INPUT_SPEED, () => _speedMult.ToString("F1"));
        /// </summary>
        public static void SetInputDefault(int inputID, System.Func<string> getter)
        {
            _inputDefaults[inputID] = getter;
        }

        /// <summary>Sets a default value getter for an input field, applying the mod's ID offset.</summary>
        public static void SetInputDefault(ModHandle handle, int inputID, System.Func<string> getter)
        {
            _inputDefaults[inputID + (handle?.IdOffset ?? 0)] = getter;
        }

        /// <summary>
        /// Gets the offset-adjusted button click event for a mod's button ID.
        /// Use this instead of OnUIButtonClick[id] to avoid cross-mod conflicts.
        ///   UIButtonAPI.GetButtonEvent(handle, BTN_MY_BTN).AddListener(() => DoThing());
        /// </summary>
        public static UnityEvent GetButtonEvent(ModHandle handle, int buttonID)
        {
            int globalID = buttonID + (handle?.IdOffset ?? 0);
            if (!OnUIButtonClick.ContainsKey(globalID)) OnUIButtonClick[globalID] = new UnityEvent();
            return OnUIButtonClick[globalID];
        }

        /// <summary>Gets the offset-adjusted toggle change event for a mod's toggle ID.</summary>
        public static UnityEvent<bool> GetToggleEvent(ModHandle handle, int toggleID)
        {
            int globalID = toggleID + (handle?.IdOffset ?? 0);
            if (!OnUIToggleChange.ContainsKey(globalID)) OnUIToggleChange[globalID] = new UnityEvent<bool>();
            return OnUIToggleChange[globalID];
        }

        /// <summary>Gets the offset-adjusted input submit event for a mod's input ID.</summary>
        public static UnityEvent<string> GetInputEvent(ModHandle handle, int inputID)
        {
            int globalID = inputID + (handle?.IdOffset ?? 0);
            if (!OnUIInputSubmit.ContainsKey(globalID)) OnUIInputSubmit[globalID] = new UnityEvent<string>();
            return OnUIInputSubmit[globalID];
        }

        /// <summary>
        /// Syncs a single toggle's visual isOn state to match a value — call after loading config.
        /// This sets the toggle WITHOUT firing onValueChanged listeners.
        ///
        /// Example:
        ///   UIButtonAPI.UIButtonAPI.SyncToggle(TGL_ESP, _esp);
        /// </summary>
        public static void SyncToggle(int toggleID, bool value)
        {
            if (!_toggleComponents.TryGetValue(toggleID, out Toggle tog) || tog == null) return;
            tog.SetIsOnWithoutNotify(value);
        }

        /// <summary>Sync a toggle using a handle — automatically applies the mod's ID offset.</summary>
        public static void SyncToggle(ModHandle handle, int toggleID, bool value)
        {
            SyncToggle(toggleID + (handle?.IdOffset ?? 0), value);
        }

        /// <summary>
        /// Syncs multiple toggles at once using a handle for auto ID offset.
        /// Pass pairs of (toggleID, value).
        /// </summary>
        public static void SyncToggles(ModHandle handle, params (int id, bool value)[] pairs)
        {
            int offset = handle?.IdOffset ?? 0;
            foreach (var (id, value) in pairs)
                SyncToggle(id + offset, value);
        }

        /// <summary>Legacy overload — no offset applied. Use the ModHandle overload instead.</summary>
        public static void SyncToggles(params (int id, bool value)[] pairs)
        {
            foreach (var (id, value) in pairs)
                SyncToggle(id, value);
        }

        /// <summary>Adds a toggle inside a sub-menu.</summary>
        public static GameObject MakeToggleInSubMenu(ModHandle handle, int subMenuID, int gridX, int gridY, string text, int toggleID)
        {
            var sub = GetSubMenu(handle, subMenuID);
            if (sub == null) { MelonLogger.Warning($"UIButtonAPI: MakeToggleInSubMenu — invalid subID {subMenuID}."); return null; }
            return SpawnToggle(sub, gridX, gridY, text, toggleID, handle?.IdOffset ?? 0);
        }

        #endregion

        // ──────────────────────────────────────────────────────────────

        // ══════════════════════════════════════════════════════════════
        //  BIG MENU API
        //  Adds pages alongside the existing Worlds/Avatar/Social pages
        //  inside Menu_Expanded.
        //
        //  Grid: (0,0)=top-left, step=65 units per cell.
        //  X = -255 + col*65,  Y = 70 - row*65
        //  Back button is NOT auto-added — big pages have no back button.
        //
        //  Usage:
        //    int page = UIButtonAPI.CreateBigPage(handle, "My Page");
        //    UIButtonAPI.MakeBigButton(handle, page, 0, 0, "Do Thing", 900);
        //    UIButtonAPI.OnUIButtonClick[900] += () => DoThing();
        // ══════════════════════════════════════════════════════════════

        /// <summary>Converts a BigMenu grid position to a Unity local position.</summary>
        public static Vector3 BigGridToUnity(int col, int row) =>
            new Vector3(BigGridOriginX + col * BigGridStep, BigGridOriginY - row * BigGridStep, 0f);

        /// <summary>
        /// Creates a new page in Menu_Expanded and returns its index.
        /// The page is hidden by default — show it by calling ShowBigPage(handle, pageID).
        /// A title is displayed in the TitlePanel/TitleText TMP.
        /// </summary>
        public static int CreateBigPage(ModHandle handle, string title)
        {
            if (handle == null) { MelonLogger.Warning("UIButtonAPI: CreateBigPage — null handle."); return -1; }
            if (_localPlayer == null) { MelonLogger.Warning("UIButtonAPI: CreateBigPage — no local player."); return -1; }
            if (_bigPagePrefab == null) { MelonLogger.Warning("UIButtonAPI: OLD_BIGMENUPAGE prefab missing."); return -1; }

            Transform menuExpanded = _localPlayer.transform.Find("Camera Offset/UI/Menu_Expanded");
            if (menuExpanded == null) { MelonLogger.Warning("UIButtonAPI: Menu_Expanded not found."); return -1; }

            GameObject page = GameObject.Instantiate(_bigPagePrefab, menuExpanded);
            page.transform.localPosition = new Vector3(0.40077f, 4.253306f, 0f);
            page.transform.localScale = new Vector3(2.501825f, 2.501825f, 2.501825f);
            page.SetActive(false);

            // Set title via TMP
            Transform titleT = page.transform.Find("TitlePanel/TitleText");
            if (titleT != null)
            {
                var tmp = titleT.GetComponent<TMPro.TextMeshProUGUI>();
                if (tmp != null) tmp.text = title;
            }

            // Hook the page's built-in close button to restore the QM
            foreach (var btn in page.GetComponentsInChildren<Button>(true))
            {
                // The close button is typically named "ExitButton", "CloseButton", or "ButtonClose"
                string n = btn.gameObject.name.ToLower();
                if (n.Contains("exit") || n.Contains("close"))
                {
                    ModHandle h = handle;
                    btn.onClick.AddListener(() => CloseBigMenu(h));
                    break;
                }
            }

            int id = handle.BigPages.Count;
            handle.BigPages.Add(page);
            return id;
        }

        /// <summary>Shows a big page, hiding all other big pages for this mod. Hides the QM while open.</summary>
        public static void ShowBigPage(ModHandle handle, int pageID)
        {
            if (handle == null || pageID < 0 || pageID >= handle.BigPages.Count) return;
            foreach (var p in handle.BigPages) p.SetActive(false);
            handle.BigPages[pageID].SetActive(true);
            handle.ActiveBigPage = handle.BigPages[pageID];
        }

        /// <summary>
        /// Opens a sub-page of the big menu, hiding the current active page.
        /// The auto-added Back button on the sub-page will return to parentPageID.
        /// </summary>
        public static void OpenSubBigPage(ModHandle handle, int parentPageID, int subPageID)
        {
            if (handle == null) return;
            if (parentPageID >= 0 && parentPageID < handle.BigPages.Count)
                handle.BigPages[parentPageID].SetActive(false);
            if (subPageID >= 0 && subPageID < handle.BigPages.Count)
            {
                handle.BigPages[subPageID].SetActive(true);
                handle.ActiveBigPage = handle.BigPages[subPageID];
            }
        }

        /// <summary>Hides all big pages for this mod and restores the QM.</summary>
        public static void HideAllBigPages(ModHandle handle)
        {
            if (handle == null) return;
            foreach (var p in handle.BigPages) p.SetActive(false);
            handle.ActiveBigPage = null;
        }

        /// <summary>
        /// Creates a big sub-page with an auto Back button that returns to parentPageID.
        /// Returns the new subPageID.
        /// </summary>
        public static int CreateSubBigPage(ModHandle handle, string title, int parentPageID)
        {
            int id = CreateBigPage(handle, title);
            if (id < 0) return -1;

            // Add a Back button at top-right (col 8, row 0) that returns to parent
            int backBtnID = -2000 - id; // unique internal ID, won't collide with user IDs
            if (!OnUIButtonClick.ContainsKey(backBtnID)) OnUIButtonClick[backBtnID] = new UnityEvent();
            var page = handle.BigPages[id];
            GameObject backBtn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            backBtn.transform.localPosition = BigGridToUnity(8, 0);
            SetTMP(backBtn, "ButtonText", "< Back");
            var btnComp = backBtn.GetComponent<Button>();
            if (btnComp != null)
            {
                int pid = parentPageID;
                btnComp.onClick.AddListener(() => ShowBigPage(handle, pid));
            }
            return id;
        }

        /// <summary>Closes all big pages for this mod and restores the QM.</summary>
        public static void CloseBigMenu(ModHandle handle)
        {
            HideAllBigPages(handle);
        }

        /// <summary>Returns the raw big page GameObject for a given pageID.</summary>
        public static GameObject GetBigPage(ModHandle handle, int pageID)
        {
            if (handle == null || pageID < 0 || pageID >= handle.BigPages.Count) return null;
            return handle.BigPages[pageID];
        }

        /// <summary>Adds a button to a big page at grid (col, row).</summary>
        public static GameObject MakeBigButton(ModHandle handle, int pageID, int col, int row, string text, int buttonID)
        {
            var page = GetBigPage(handle, pageID);
            if (page == null || _bigBtnPrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigButton — invalid page or missing prefab."); return null; }
            return SpawnBigButton(page, col, row, text, buttonID, handle?.IdOffset ?? 0);
        }

        /// <summary>Adds a toggle to a big page at grid (col, row).</summary>
        public static GameObject MakeBigToggle(ModHandle handle, int pageID, int col, int row, string text, int toggleID)
        {
            var page = GetBigPage(handle, pageID);
            if (page == null || _bigTogglePrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigToggle — invalid page or missing prefab."); return null; }
            return SpawnBigToggle(page, col, row, text, toggleID, handle?.IdOffset ?? 0);
        }

        /// <summary>Adds a text comment label to a big page at grid (col, row).</summary>
        public static GameObject MakeBigComment(ModHandle handle, int pageID, int col, int row, string text)
        {
            var page = GetBigPage(handle, pageID);
            if (page == null || _bigCommentPrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigComment — invalid page or missing prefab."); return null; }

            GameObject obj = GameObject.Instantiate(_bigCommentPrefab, page.transform);
            obj.transform.localPosition = BigGridToUnity(col, row);
            // Comment uses TMP directly on itself or a child — try both
            var tmp = obj.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = text;
            else
            {
                Transform t = obj.transform.Find("ButtonText");
                if (t != null) { var t2 = t.GetComponent<TMPro.TextMeshProUGUI>(); if (t2 != null) t2.text = text; }
            }
            return obj;
        }

        /// <summary>
        /// Creates an InputField button on a big page that opens the Input Popup.
        /// Requires the local player GO.
        /// </summary>
        public static GameObject MakeBigInputField(ModHandle handle, GameObject localPlayer,
            int pageID, int col, int row, string label, int inputID)
        {
            var page = GetBigPage(handle, pageID);
            if (page == null || _bigBtnPrefab == null) { MelonLogger.Warning("UIButtonAPI: MakeBigInputField — invalid page or missing prefab."); return null; }

            if (!OnUIInputSubmit.ContainsKey(inputID)) OnUIInputSubmit[inputID] = new UnityEvent<string>();

            GameObject btn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            btn.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(btn, "ButtonText", label);

            var btnComp = btn.GetComponent<Button>();
            if (btnComp != null)
            {
                int id = inputID; string lbl = label;
                btnComp.onClick.AddListener(() =>
                {
                    string def = _inputDefaults.ContainsKey(id) ? _inputDefaults[id]() : "";
                    OpenInputPopup(localPlayer, lbl, id, def);
                });
            }
            return btn;
        }

        #region Internal Helpers

        private static GameObject SpawnButton(GameObject panel, int gridX, int gridY, string text, int buttonID, int idOffset = 0)
        {
            int globalID = buttonID + idOffset;
            if (!OnUIButtonClick.ContainsKey(globalID)) OnUIButtonClick[globalID] = new UnityEvent();
            GameObject btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(gridX, gridY);
            SetText(btn, "ButtonText", text);
            var comp = btn.GetComponent<Button>();
            if (comp != null) { int id = globalID; comp.onClick.AddListener(() => OnUIButtonClick[id]?.Invoke()); }
            return btn;
        }

        private static GameObject SpawnToggle(GameObject panel, int gridX, int gridY, string text, int toggleID, int idOffset = 0)
        {
            int globalID = toggleID + idOffset;
            if (!OnUIToggleChange.ContainsKey(globalID)) OnUIToggleChange[globalID] = new UnityEvent<bool>();
            GameObject obj = GameObject.Instantiate(_togglePrefab, panel.transform);
            obj.transform.localPosition = GridToUnity(gridX, gridY);
            SetText(obj, "ButtonText", text);
            var comp = obj.GetComponent<Toggle>();
            if (comp != null)
            {
                int id = globalID;
                _toggleComponents[id] = comp;
                comp.onValueChanged.AddListener(isOn => OnUIToggleChange[id]?.Invoke(isOn));
            }
            return obj;
        }

        private static GameObject SpawnBigButton(GameObject page, int col, int row, string text, int buttonID, int idOffset = 0)
        {
            int globalID = buttonID + idOffset;
            if (!OnUIButtonClick.ContainsKey(globalID)) OnUIButtonClick[globalID] = new UnityEvent();
            GameObject btn = GameObject.Instantiate(_bigBtnPrefab, page.transform);
            btn.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(btn, "ButtonText", text);
            var comp = btn.GetComponent<Button>();
            if (comp != null) { int id = globalID; comp.onClick.AddListener(() => OnUIButtonClick[id]?.Invoke()); }
            return btn;
        }

        private static GameObject SpawnBigToggle(GameObject page, int col, int row, string text, int toggleID, int idOffset = 0)
        {
            int globalID = toggleID + idOffset;
            if (!OnUIToggleChange.ContainsKey(globalID)) OnUIToggleChange[globalID] = new UnityEvent<bool>();
            GameObject obj = GameObject.Instantiate(_bigTogglePrefab, page.transform);
            obj.transform.localPosition = BigGridToUnity(col, row);
            SetTMP(obj, "ButtonText", text);
            var comp = obj.GetComponent<Toggle>();
            if (comp != null)
            {
                int id = globalID;
                _toggleComponents[id] = comp;
                comp.onValueChanged.AddListener(isOn => OnUIToggleChange[id]?.Invoke(isOn));
            }
            return obj;
        }

        // Sets a TMP UI text child — used for BigMenu elements
        private static void SetTMP(GameObject parent, string childName, string text)
        {
            Transform t = parent.transform.Find(childName);
            if (t == null) return;
            var tmp = t.GetComponent<TMPro.TextMeshProUGUI>();
            if (tmp != null) tmp.text = text;
        }

        private static void SetText(GameObject parent, string childName, string text)
        {
            Transform t = parent.transform.Find(childName);
            if (t == null) return;
            var leg = t.GetComponent<Text>();
            if (leg != null) leg.text = text;
        }

        #endregion
    }
}