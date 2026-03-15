using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun;

[assembly: MelonInfo(typeof(UIButtonAPI.UIButtonAPI), "UIButtonAPI", "2.0.0", "Lumarizle + AI")]
[assembly: MelonGame]

namespace UIButtonAPI
{
    public class UIButtonAPI : MelonMod
    {
        // ── Event Tables ───────────────────────────────────────────────
        //
        // Subscribe from anywhere in your mod after OnUIReady fires:
        //
        //   UIButtonAPI.OnUIReady              += SetupMyUI;
        //   UIButtonAPI.OnUIButtonClick[1]     += MyButtonHandler;
        //   UIButtonAPI.OnUIToggleChange[1]    += isOn => MelonLogger.Msg(isOn);
        //
        // Grid coordinates: (0,0) = Top-Left, (3,2) = Bottom-Right
        // X range: 0–3, Y range: 0–2  (420 units per cell, negatives allowed)
        // Maps to Unity local positions: X = -630 + gridX * 420,  Y = 1471.6 - gridY * 420
        //
        public static UnityEvent OnUIReady = new UnityEvent();
        public static Dictionary<int, UnityEvent> OnUIButtonClick = new Dictionary<int, UnityEvent>();
        public static Dictionary<int, UnityEvent<bool>> OnUIToggleChange = new Dictionary<int, UnityEvent<bool>>();

        // ── Internal References ────────────────────────────────────────
        private static GameObject _localPlayer;
        private static GameObject _instantiatedMenu;
        private static Transform _shortcutMenu;
        private static bool _uiBuilt = false;

        // Cached prefabs
        private static GameObject _btnPrefab;
        private static GameObject _togglePrefab;

        // ── Sub-menu system ────────────────────────────────────────────
        private static Dictionary<int, GameObject> _subMenus = new Dictionary<int, GameObject>();
        private static GameObject _activeSubMenu = null;

        // ── Grid → Unity coordinate conversion constants ───────────────
        // Grid is 3×2 (indices 0–3 on X, 0–2 on Y), 420 units per cell.
        // (0,0)=Top-Left=(-630, 1471.6)  step=420 on both axes.
        // Negative indices and out-of-bounds indices are allowed.
        private const float GridOriginX = -630f;
        private const float GridOriginY = 1471.6f;
        private const float GridStepX = 420f;
        private const float GridStepY = -420f;

        // ──────────────────────────────────────────────────────────────
        #region MelonLoader Callbacks

        public override void OnApplicationStart()
        {
            MelonLogger.Msg("UIButtonAPI v2.0 loaded.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _uiBuilt = false;
            _localPlayer = null;
            _instantiatedMenu = null;
            _shortcutMenu = null;
            _btnPrefab = null;
            _togglePrefab = null;
            _subMenus.Clear();
            _activeSubMenu = null;

            OnUIReady = new UnityEvent();
            OnUIButtonClick.Clear();
            OnUIToggleChange.Clear();

            MelonCoroutines.Start(PollForLocalPlayer());
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Player Polling

        private static System.Collections.IEnumerator PollForLocalPlayer()
        {
            MelonLogger.Msg("UIButtonAPI: Searching for local player...");

            while (_localPlayer == null)
            {
                foreach (var go in GameObject.FindObjectsOfType<GameObject>())
                {
                    if (!go.name.StartsWith("PhotonDesktopPlayer")) continue;
                    var pv = go.GetComponent<PhotonView>();
                    if (pv != null && pv.IsMine)
                    {
                        _localPlayer = go;
                        MelonLogger.Msg($"UIButtonAPI: Local player found -> {go.name}");
                        break;
                    }
                }

                if (_localPlayer == null)
                    yield return new WaitForSeconds(1f);
            }

            if (!_uiBuilt)
                BuildUI();
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Core UI Setup

        private static void BuildUI()
        {
            // ── Find QuickMenu ──────────────────────────────────────────
            Transform quickMenu = _localPlayer.transform.Find(
                "Camera Offset/UI/Menu_Small/QM/QuickMenu"
            );

            if (quickMenu == null)
            {
                MelonLogger.Warning("UIButtonAPI: Could not find QuickMenu under local player.");
                return;
            }

            // ── Find ShortcutMenu ───────────────────────────────────────
            _shortcutMenu = _localPlayer.transform.Find(
                "Camera Offset/UI/Menu_Small/QM/QuickMenu/ShortcutMenu"
            );

            if (_shortcutMenu == null)
                MelonLogger.Warning("UIButtonAPI: Could not find ShortcutMenu – shortcut button will be skipped.");

            // ── Load prefabs ────────────────────────────────────────────
            GameObject menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            _btnPrefab = Resources.Load<GameObject>("OLD_BUTTON");
            _togglePrefab = Resources.Load<GameObject>("OLD_TOGGLE");

            if (menuPrefab == null || _btnPrefab == null || _togglePrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: One or more prefabs missing from Resources.");
                MelonLogger.Msg($"  menu={menuPrefab != null}  btn={_btnPrefab != null}  toggle={_togglePrefab != null}");
                return;
            }

            // ── Instantiate main menu panel (hidden by default) ─────────
            _instantiatedMenu = GameObject.Instantiate(menuPrefab, quickMenu);
            _instantiatedMenu.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
            _instantiatedMenu.SetActive(false);

            // ── Shortcut button (Mods) in ShortcutMenu ──────────────────
            if (_shortcutMenu != null)
            {
                GameObject shortcutBtn = GameObject.Instantiate(_btnPrefab, _shortcutMenu);
                shortcutBtn.transform.localPosition = GridToUnity(-1, 0);
                SetChildText(shortcutBtn, "ButtonText", "Mods");

                var btnComp = shortcutBtn.GetComponent<Button>();
                if (btnComp != null)
                    btnComp.onClick.AddListener(() => OpenMainMenu());
                else
                    MelonLogger.Warning("UIButtonAPI: Shortcut button has no Button component.");

                MelonLogger.Msg("UIButtonAPI: Shortcut button created in ShortcutMenu at grid (-1, 0).");
            }

            // ── Back button at  in main menu ──────────────────────
            // Outside the normal grid area — closes mod menu, restores ShortcutMenu.
            GameObject backBtn = GameObject.Instantiate(_btnPrefab, _instantiatedMenu.transform);
            backBtn.transform.localPosition = GridToUnity(3, 2);
            SetChildText(backBtn, "ButtonText", "Back");
            var backComp = backBtn.GetComponent<Button>();
            if (backComp != null)
                backComp.onClick.AddListener(() => CloseMainMenu());
            MelonLogger.Msg("UIButtonAPI: Back button created");

            // ── Example layout (remove / change these as you like) ──────
            MakeButton(0, 0, "Cool Button!", 1);
            MakeToggle(1, 0, "My Toggle", 1);

            // ── Fire OnUIReady ──────────────────────────────────────────
            _uiBuilt = true;
            MelonLogger.Msg("UIButtonAPI: UI built - firing OnUIReady.");
            OnUIReady?.Invoke();
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Main Menu Open / Close

        private static void OpenMainMenu()
        {
            if (_activeSubMenu != null)
            {
                _activeSubMenu.SetActive(false);
                _activeSubMenu = null;
            }

            _instantiatedMenu.SetActive(true);
            if (_shortcutMenu != null)
                _shortcutMenu.gameObject.SetActive(false);

            MelonLogger.Msg("UIButtonAPI: Main menu opened.");
        }

        private static void CloseMainMenu()
        {
            _instantiatedMenu.SetActive(false);
            if (_shortcutMenu != null)
                _shortcutMenu.gameObject.SetActive(true);

            MelonLogger.Msg("UIButtonAPI: Main menu closed.");
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Sub-Menu System

        /// <summary>
        /// Creates a new sub-menu panel and returns its ID.
        /// The sub-menu is hidden by default. Use OpenSubMenu(id) to show it.
        /// A Back button is added automatically, returning to the main menu.
        ///
        /// Example:
        ///   // In OnUIReady handler:
        ///   int movID = UIButtonAPI.CreateSubMenu("Movement");
        ///
        ///   UIButtonAPI.MakeButton(0, 0, "Movement", 1);
        ///   UIButtonAPI.OnUIButtonClick[1] += () => UIButtonAPI.OpenSubMenu(movID);
        ///
        ///   UIButtonAPI.MakeToggleInSubMenu(movID, 0, 0, "No-Clip", 101);
        ///   UIButtonAPI.OnUIToggleChange[101] += isOn => SetNoClip(isOn);
        /// </summary>
        public static int CreateSubMenu(string title)
        {
            if (_instantiatedMenu == null || _btnPrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: CreateSubMenu called before UI is ready.");
                return -1;
            }

            GameObject menuPrefab = Resources.Load<GameObject>("OLD_MENU");
            if (menuPrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: CreateSubMenu – OLD_MENU prefab missing.");
                return -1;
            }

            // Parent to the same QuickMenu as the main menu
            Transform parent = _instantiatedMenu.transform.parent;
            GameObject sub = GameObject.Instantiate(menuPrefab, parent);
            sub.transform.localPosition = new Vector3(9.33f, -755.0295f, 0f);
            sub.SetActive(false);

            // Unique ID: offset by 1000 to avoid colliding with button IDs
            int id = 1000 + _subMenus.Count;
            _subMenus[id] = sub;

            // Auto Back button 
            GameObject backBtn = GameObject.Instantiate(_btnPrefab, sub.transform);
            backBtn.transform.localPosition = GridToUnity(3, 2);
            SetChildText(backBtn, "ButtonText", "Back");
            var backComp = backBtn.GetComponent<Button>();
            if (backComp != null)
                backComp.onClick.AddListener(() => CloseSubMenu());

            MelonLogger.Msg($"UIButtonAPI: Sub-menu '{title}' created (subID={id}).");
            return id;
        }

        /// <summary>
        /// Opens a sub-menu, hiding the main menu.
        /// Call with the ID returned from CreateSubMenu.
        /// </summary>
        public static void OpenSubMenu(int subMenuID)
        {
            if (!_subMenus.TryGetValue(subMenuID, out GameObject sub))
            {
                MelonLogger.Warning($"UIButtonAPI: OpenSubMenu – unknown subMenuID {subMenuID}.");
                return;
            }

            if (_activeSubMenu != null)
                _activeSubMenu.SetActive(false);
            else
                _instantiatedMenu.SetActive(false);

            sub.SetActive(true);
            _activeSubMenu = sub;
            MelonLogger.Msg($"UIButtonAPI: Opened sub-menu {subMenuID}.");
        }

        /// <summary>Closes the active sub-menu and returns to the main menu.</summary>
        public static void CloseSubMenu()
        {
            if (_activeSubMenu != null)
            {
                _activeSubMenu.SetActive(false);
                _activeSubMenu = null;
            }

            _instantiatedMenu.SetActive(true);
            MelonLogger.Msg("UIButtonAPI: Closed sub-menu, returned to main menu.");
        }

        /// <summary>Returns the raw sub-menu GameObject for a given subMenuID, or null.</summary>
        public static GameObject GetSubMenu(int subMenuID)
        {
            _subMenus.TryGetValue(subMenuID, out GameObject sub);
            return sub;
        }

        /// <summary>
        /// Instantiates an OLD_BUTTON inside a sub-menu at grid (gridX, gridY).
        /// Uses the shared OnUIButtonClick event table.
        /// </summary>
        public static GameObject MakeButtonInSubMenu(int subMenuID, int gridX, int gridY, string text, int buttonID)
        {
            if (!_subMenus.TryGetValue(subMenuID, out GameObject sub))
            {
                MelonLogger.Warning($"UIButtonAPI: MakeButtonInSubMenu – unknown subMenuID {subMenuID}.");
                return null;
            }

            if (!OnUIButtonClick.ContainsKey(buttonID))
                OnUIButtonClick[buttonID] = new UnityEvent();

            GameObject btn = GameObject.Instantiate(_btnPrefab, sub.transform);
            btn.transform.localPosition = GridToUnity(gridX, gridY);
            SetChildText(btn, "ButtonText", text);

            var btnComp = btn.GetComponent<Button>();
            if (btnComp != null)
            {
                int id = buttonID;
                btnComp.onClick.AddListener(() =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIButtonClick[{id}] fired (sub-menu).");
                    OnUIButtonClick[id]?.Invoke();
                });
            }

            MelonLogger.Msg($"UIButtonAPI: MakeButtonInSubMenu (subID={subMenuID}, ID={buttonID}, grid=({gridX},{gridY}), text=\"{text}\")");
            return btn;
        }

        /// <summary>
        /// Instantiates an OLD_TOGGLE inside a sub-menu at grid (gridX, gridY).
        /// Uses the shared OnUIToggleChange event table.
        /// </summary>
        public static GameObject MakeToggleInSubMenu(int subMenuID, int gridX, int gridY, string text, int toggleID)
        {
            if (!_subMenus.TryGetValue(subMenuID, out GameObject sub))
            {
                MelonLogger.Warning($"UIButtonAPI: MakeToggleInSubMenu – unknown subMenuID {subMenuID}.");
                return null;
            }

            if (!OnUIToggleChange.ContainsKey(toggleID))
                OnUIToggleChange[toggleID] = new UnityEvent<bool>();

            GameObject toggleObj = GameObject.Instantiate(_togglePrefab, sub.transform);
            toggleObj.transform.localPosition = GridToUnity(gridX, gridY);
            SetChildText(toggleObj, "ButtonText", text);

            var toggleComp = toggleObj.GetComponent<Toggle>();
            if (toggleComp != null)
            {
                int id = toggleID;
                toggleComp.onValueChanged.AddListener((isOn) =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIToggleChange[{id}] fired (sub-menu, isOn={isOn}).");
                    OnUIToggleChange[id]?.Invoke(isOn);
                });
            }

            MelonLogger.Msg($"UIButtonAPI: MakeToggleInSubMenu (subID={subMenuID}, ID={toggleID}, grid=({gridX},{gridY}), text=\"{text}\")");
            return toggleObj;
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Public UI API

        /// <summary>
        /// Converts a grid cell (gridX, gridY) to a Unity local position.
        /// (0,0) = Top-Left. Negatives and out-of-bounds values are fine.
        /// </summary>
        public static Vector3 GridToUnity(int gridX, int gridY)
        {
            float x = GridOriginX + gridX * GridStepX;
            float y = GridOriginY + gridY * GridStepY;
            return new Vector3(x, y, 0f);
        }

        /// <summary>
        /// Instantiates an OLD_BUTTON in the main menu at grid (gridX, gridY).
        ///
        /// Example:
        ///   MakeButton(0, 0, "Say Hi", 1);
        ///   UIButtonAPI.OnUIButtonClick[1] += () => MelonLogger.Msg("Hi!");
        /// </summary>
        public static GameObject MakeButton(int gridX, int gridY, string text, int buttonID)
        {
            if (_instantiatedMenu == null || _btnPrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: MakeButton({buttonID}) called before UI is ready.");
                return null;
            }

            if (!OnUIButtonClick.ContainsKey(buttonID))
                OnUIButtonClick[buttonID] = new UnityEvent();

            GameObject btn = GameObject.Instantiate(_btnPrefab, _instantiatedMenu.transform);
            btn.transform.localPosition = GridToUnity(gridX, gridY);
            SetChildText(btn, "ButtonText", text);

            var btnComp = btn.GetComponent<Button>();
            if (btnComp != null)
            {
                int id = buttonID;
                btnComp.onClick.AddListener(() =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIButtonClick[{id}] fired.");
                    OnUIButtonClick[id]?.Invoke();
                });
            }
            else
            {
                MelonLogger.Warning($"UIButtonAPI: MakeButton({buttonID}) – no Button component on prefab.");
            }

            MelonLogger.Msg($"UIButtonAPI: MakeButton created (ID={buttonID}, grid=({gridX},{gridY}), text=\"{text}\")");
            return btn;
        }

        /// <summary>
        /// Instantiates an OLD_TOGGLE in the main menu at grid (gridX, gridY).
        ///
        /// Example:
        ///   MakeToggle(0, 1, "God Mode", 1);
        ///   UIButtonAPI.OnUIToggleChange[1] += isOn => godModeEnabled = isOn;
        /// </summary>
        public static GameObject MakeToggle(int gridX, int gridY, string text, int toggleID)
        {
            if (_instantiatedMenu == null || _togglePrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: MakeToggle({toggleID}) called before UI is ready.");
                return null;
            }

            if (!OnUIToggleChange.ContainsKey(toggleID))
                OnUIToggleChange[toggleID] = new UnityEvent<bool>();

            GameObject toggleObj = GameObject.Instantiate(_togglePrefab, _instantiatedMenu.transform);
            toggleObj.transform.localPosition = GridToUnity(gridX, gridY);
            SetChildText(toggleObj, "ButtonText", text);

            var toggleComp = toggleObj.GetComponent<Toggle>();
            if (toggleComp != null)
            {
                int id = toggleID;
                toggleComp.onValueChanged.AddListener((isOn) =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIToggleChange[{id}] fired (isOn={isOn}).");
                    OnUIToggleChange[id]?.Invoke(isOn);
                });
            }
            else
            {
                MelonLogger.Warning($"UIButtonAPI: MakeToggle({toggleID}) – no Toggle component on prefab.");
            }

            MelonLogger.Msg($"UIButtonAPI: MakeToggle created (ID={toggleID}, grid=({gridX},{gridY}), text=\"{text}\")");
            return toggleObj;
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Helpers

        /// <summary>
        /// Finds a named child and sets its legacy Unity Text component.
        /// (ButtonText uses legacy Text, not TextMeshProUGUI, as of the game update.)
        /// </summary>
        private static void SetChildText(GameObject parent, string childName, string text)
        {
            Transform t = parent.transform.Find(childName);
            if (t == null)
            {
                MelonLogger.Warning($"UIButtonAPI: Child '{childName}' not found on {parent.name}.");
                return;
            }

            var legacyText = t.GetComponent<Text>();
            if (legacyText != null)
            {
                legacyText.text = text;
                return;
            }

            MelonLogger.Warning($"UIButtonAPI: '{childName}' has no legacy Text component on {parent.name}.");
        }

        #endregion
    }
}