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
//       UIButtonAPI.UIButtonAPI.OnUIButtonClick[1] += () => MelonLogger.Msg("Hi!");
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

        // Shortcut button position.
        // If ShortcutGridX is int.MinValue the button is auto-stacked vertically at (-1, ID).
        // Set these via RegisterMod() overload or manually before the scene loads.
        public int ShortcutGridX = int.MinValue;
        public int ShortcutGridY = int.MinValue;

        internal GameObject MenuPanel;
        internal List<GameObject> SubMenus = new List<GameObject>();
        internal GameObject ActiveSubMenu = null;
        internal bool IsOpen = false;
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

        // Prefabs
        private static GameObject _btnPrefab;
        private static GameObject _togglePrefab;

        // ── Grid constants ─────────────────────────────────────────────
        private const float GridOriginX = -630f;
        private const float GridOriginY = 1471.6f;
        private const float GridStepX = 420f;
        private const float GridStepY = -420f;

        // ──────────────────────────────────────────────────────────────
        #region MelonLoader Lifecycle

        public override void OnApplicationStart()
        {
            MelonLogger.Msg("UIButtonAPI v3.0 loaded — multi-mod support.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _uiBuilt = false;
            MainMenuReady = false;
            _localPlayer = null;
            _shortcutMenu = null;
            _btnPrefab = null;
            _togglePrefab = null;

            // Reset all handle state but keep the handle registrations themselves
            foreach (var h in _handles)
            {
                h.MenuPanel = null;
                h.SubMenus.Clear();
                h.ActiveSubMenu = null;
                h.IsOpen = false;
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
            };
            _handles.Add(handle);
            _pendingHandles.Add(handle);
            MelonLogger.Msg($"UIButtonAPI: Registered mod '{name}' (ID={handle.ID}, " +
                $"shortcut={(shortcutGridX == int.MinValue ? "auto-stack" : $"({shortcutGridX},{shortcutGridY})")})");
            return handle;
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Player Polling

        private static System.Collections.IEnumerator WaitForLocalPlayer()
        {
            MelonLogger.Msg("UIButtonAPI: Waiting for local player...");
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

            if (menuPrefab == null || _btnPrefab == null || _togglePrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: Missing prefabs — menu={menuPrefab != null} btn={_btnPrefab != null} toggle={_togglePrefab != null}");
                return;
            }

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

                    MelonLogger.Msg($"UIButtonAPI: Built menu for '{handle.Name}' at shortcut ({bx},{by}).");
                }
            }

            _pendingHandles.Clear();
            _uiBuilt = true;
            MainMenuReady = true;
            MelonLogger.Msg("UIButtonAPI: Ready — firing OnUIReady.");
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
            MelonLogger.Msg($"UIButtonAPI: Opened '{handle.Name}'");
        }

        private static void CloseMainMenu(ModHandle handle)
        {
            if (handle.MenuPanel != null) handle.MenuPanel.SetActive(false);
            handle.IsOpen = false;

            // Only restore ShortcutMenu if no other mod is open
            bool anyOpen = false;
            foreach (var h in _handles) if (h.IsOpen) { anyOpen = true; break; }
            if (!anyOpen && _shortcutMenu != null) _shortcutMenu.gameObject.SetActive(true);

            MelonLogger.Msg($"UIButtonAPI: Closed '{handle.Name}'");
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
            MelonLogger.Msg($"UIButtonAPI: Sub-menu '{title}' created for '{handle.Name}' (subID={id}).");
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
            return SpawnButton(handle.MenuPanel, gridX, gridY, text, buttonID);
        }

        /// <summary>Adds a toggle to the mod's MAIN menu.</summary>
        public static GameObject MakeToggle(ModHandle handle, int gridX, int gridY, string text, int toggleID)
        {
            if (handle?.MenuPanel == null) { MelonLogger.Warning($"UIButtonAPI: MakeToggle called before UI ready."); return null; }
            return SpawnToggle(handle.MenuPanel, gridX, gridY, text, toggleID);
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

            MelonLogger.Msg($"UIButtonAPI: InputField '{label}' (ID={inputID}) at ({gridX},{gridY}).");
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
                    MelonLogger.Msg($"UIButtonAPI: Input submitted (ID={id}, value='{value}')");
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

        /// <summary>
        /// Syncs multiple toggles at once. Pass pairs of (toggleID, value).
        /// Example:
        ///   UIButtonAPI.UIButtonAPI.SyncToggles((TGL_ESP, _esp), (TGL_FLY, _fly));
        /// </summary>
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
            return SpawnToggle(sub, gridX, gridY, text, toggleID);
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Internal Helpers

        private static GameObject SpawnButton(GameObject panel, int gridX, int gridY, string text, int buttonID)
        {
            if (!OnUIButtonClick.ContainsKey(buttonID)) OnUIButtonClick[buttonID] = new UnityEvent();
            GameObject btn = GameObject.Instantiate(_btnPrefab, panel.transform);
            btn.transform.localPosition = GridToUnity(gridX, gridY);
            SetText(btn, "ButtonText", text);
            var comp = btn.GetComponent<Button>();
            if (comp != null) { int id = buttonID; comp.onClick.AddListener(() => OnUIButtonClick[id]?.Invoke()); }
            return btn;
        }

        private static GameObject SpawnToggle(GameObject panel, int gridX, int gridY, string text, int toggleID)
        {
            if (!OnUIToggleChange.ContainsKey(toggleID)) OnUIToggleChange[toggleID] = new UnityEvent<bool>();
            GameObject obj = GameObject.Instantiate(_togglePrefab, panel.transform);
            obj.transform.localPosition = GridToUnity(gridX, gridY);
            SetText(obj, "ButtonText", text);
            var comp = obj.GetComponent<Toggle>();
            if (comp != null)
            {
                int id = toggleID;
                _toggleComponents[id] = comp;
                comp.onValueChanged.AddListener(isOn => OnUIToggleChange[id]?.Invoke(isOn));
            }
            return obj;
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