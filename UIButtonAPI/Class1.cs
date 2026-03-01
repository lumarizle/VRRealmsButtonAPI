using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using TMPro;
using Photon.Pun;

[assembly: MelonInfo(typeof(UIButtonAPI.UIButtonAPI), "UIButtonAPI", "1.0.0", "Lumarizle + AI")]
[assembly: MelonGame]

namespace UIButtonAPI
{
    public class UIButtonAPI : MelonMod
    {
        // ── Event Tables ───────────────────────────────────────────────
        //
        // Subscribe from anywhere in your mod after OnUIReady fires:
        //
        //   UIButtonAPI.OnUIReady          += SetupMyUI;
        //   UIButtonAPI.OnUIButtonClick[1]  += MyButtonHandler;
        //   UIButtonAPI.OnUIToggleChange[1] += isOn => MelonLogger.Msg(isOn);
        //   UIButtonAPI.OnUISliderChange[1] += val  => MelonLogger.Msg(val);
        //
        public static UnityEvent OnUIReady = new UnityEvent();
        public static Dictionary<int, UnityEvent> OnUIButtonClick = new Dictionary<int, UnityEvent>();
        public static Dictionary<int, UnityEvent<bool>> OnUIToggleChange = new Dictionary<int, UnityEvent<bool>>();
        public static Dictionary<int, UnityEvent<float>> OnUISliderChange = new Dictionary<int, UnityEvent<float>>();

        // ── Internal References ────────────────────────────────────────
        private static GameObject _localPlayer;
        private static GameObject _instantiatedMenu;
        private static GameObject _instantiatedTab;
        private static bool _uiBuilt = false;

        // Cached prefabs – loaded once in BuildUI, reused by API calls
        private static GameObject _rowPrefab;
        private static GameObject _btnPrefab;
        private static GameObject _togglePrefab;
        private static GameObject _sliderPrefab;

        // ──────────────────────────────────────────────────────────────
        #region MelonLoader Callbacks

        public override void OnApplicationStart()
        {
            MelonLogger.Msg("UIButtonAPI loaded.");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _uiBuilt = false;
            _localPlayer = null;
            _instantiatedMenu = null;
            _instantiatedTab = null;
            _rowPrefab = null;
            _btnPrefab = null;
            _togglePrefab = null;
            _sliderPrefab = null;

            // Clear event tables on scene reload so IDs don't pile up
            OnUIReady = new UnityEvent();
            OnUIButtonClick.Clear();
            OnUIToggleChange.Clear();
            OnUISliderChange.Clear();

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
            // ── Find Tabs + QuickMenu_Modern ────────────────────────────
            Transform tabsTransform = _localPlayer.transform.Find(
                "Camera Offset/UI/Menu_Small/QM/QuickMenu_Modern/Tabs"
            );

            if (tabsTransform == null)
            {
                MelonLogger.Warning("UIButtonAPI: Could not find Tabs under local player.");
                return;
            }

            Transform quickMenuModern = tabsTransform.parent;

            // ── Load all prefabs ────────────────────────────────────────
            GameObject tabPrefab = Resources.Load<GameObject>("Desktop_UIMenuTab");
            GameObject menuPrefab = Resources.Load<GameObject>("Desktop_UIMenu");
            _rowPrefab = Resources.Load<GameObject>("Desktop_UIRow");
            _btnPrefab = Resources.Load<GameObject>("Desktop_UIButtonPrefab");
            _togglePrefab = Resources.Load<GameObject>("Desktop_UITogglePrefab");
            _sliderPrefab = Resources.Load<GameObject>("Desktop_UISliderPrefab");

            if (tabPrefab == null || menuPrefab == null || _rowPrefab == null ||
                _btnPrefab == null || _togglePrefab == null || _sliderPrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: One or more prefabs missing from Resources.");
                MelonLogger.Msg($"  tab={tabPrefab != null} menu={menuPrefab != null} " +
                                $"row={_rowPrefab != null} btn={_btnPrefab != null} " +
                                $"toggle={_togglePrefab != null} slider={_sliderPrefab != null}");
                return;
            }

            // ── Instantiate menu panel ──────────────────────────────────
            _instantiatedMenu = GameObject.Instantiate(menuPrefab, quickMenuModern);
            _instantiatedMenu.transform.localPosition = Vector3.zero;

            Transform titleT = _instantiatedMenu.transform.Find("Title");
            if (titleT != null)
            {
                var tmp = titleT.GetComponent<TextMeshProUGUI>();
                if (tmp != null) tmp.text = "UIButtonAPI";
            }

            // ── Instantiate tab ─────────────────────────────────────────
            _instantiatedTab = GameObject.Instantiate(tabPrefab, tabsTransform);
            SetZero(_instantiatedTab.transform);

            var tabToggle = _instantiatedTab.GetComponent<Toggle>();
            if (tabToggle != null)
            {
                var group = tabsTransform.GetComponent<ToggleGroup>();
                if (group != null) tabToggle.group = group;
            }

            var toggleObjScript = _instantiatedTab.GetComponent("ToggleObjectScript");
            if (toggleObjScript != null)
            {
                var field = toggleObjScript.GetType().GetField(
                    "targetObject",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
                );
                field?.SetValue(toggleObjScript, _instantiatedMenu);
            }

            // ── Example UI layout ───────────────────────────────────────
            // Feel free to remove or change these — they're just defaults.
            GameObject row1 = MakeRow(0f, 95f);
            MakeButton(row1, "Cool Button!", 1);
            MakeToggle(row1, "My Toggle", 1);

            // GameObject row2 = MakeRow(0f, 0f);
            // MakeButton(row2, "Another Button", 2);
            // MakeSlider(row2, "My Slider", 1, 0f, 100f);

            // ── Fire OnUIReady so other code can add its own elements ───
            _uiBuilt = true;
            MelonLogger.Msg("UIButtonAPI: UI built - firing OnUIReady.");
            OnUIReady?.Invoke();
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Public UI API

        /// <summary>
        /// Instantiates a Desktop_UIRow inside the menu at the given local XY position.
        /// The row already has a Horizontal Layout Group in-game, so children space themselves.
        /// Returns the row GameObject to pass into MakeButton / MakeToggle / MakeSlider.
        /// </summary>
        public static GameObject MakeRow(float posX, float posY)
        {
            if (_instantiatedMenu == null || _rowPrefab == null)
            {
                MelonLogger.Warning("UIButtonAPI: MakeRow called before UI is ready.");
                return null;
            }

            GameObject row = GameObject.Instantiate(_rowPrefab, _instantiatedMenu.transform);
            row.transform.localPosition = new Vector3(posX, posY, 0f);
            MelonLogger.Msg($"UIButtonAPI: MakeRow created at ({posX}, {posY})");
            return row;
        }

        /// <summary>
        /// Instantiates a Desktop_UIButtonPrefab inside the given row.
        /// Sets the ButtonText label and registers OnUIButtonClick[buttonID].
        ///
        /// Example:
        ///   var row = MakeRow(0f, 95f);
        ///   MakeButton(row, "Say Hi", 1);
        ///   OnUIButtonClick[1] += () => MelonLogger.Msg("Hi!");
        /// </summary>
        public static GameObject MakeButton(GameObject row, string text, int buttonID)
        {
            if (row == null || _btnPrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: MakeButton({buttonID}) – null row or missing prefab.");
                return null;
            }

            if (!OnUIButtonClick.ContainsKey(buttonID))
                OnUIButtonClick[buttonID] = new UnityEvent();

            GameObject btn = GameObject.Instantiate(_btnPrefab, row.transform);
            SetZero(btn.transform);
            SetChildText(btn, "ButtonText", text);

            var btnComp = btn.GetComponent<Button>();
            if (btnComp != null)
            {
                int id = buttonID;
                btnComp.onClick.AddListener(() =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIButtonClick{id} fired.");
                    OnUIButtonClick[id]?.Invoke();
                });
            }
            else
            {
                MelonLogger.Warning($"UIButtonAPI: MakeButton({buttonID}) – no Button component on prefab.");
            }

            MelonLogger.Msg($"UIButtonAPI: MakeButton created (ID={buttonID}, text=\"{text}\")");
            return btn;
        }

        /// <summary>
        /// Instantiates a Desktop_UITogglePrefab inside the given row.
        /// Sets the ButtonText label and registers OnUIToggleChange[toggleID].
        /// The event passes the new bool value: true = on, false = off.
        ///
        /// Example:
        ///   var row = MakeRow(0f, 95f);
        ///   MakeToggle(row, "God Mode", 1);
        ///   OnUIToggleChange[1] += isOn => godModeEnabled = isOn;
        /// </summary>
        public static GameObject MakeToggle(GameObject row, string text, int toggleID)
        {
            if (row == null || _togglePrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: MakeToggle({toggleID}) – null row or missing prefab.");
                return null;
            }

            if (!OnUIToggleChange.ContainsKey(toggleID))
                OnUIToggleChange[toggleID] = new UnityEvent<bool>();

            GameObject toggleObj = GameObject.Instantiate(_togglePrefab, row.transform);
            SetZero(toggleObj.transform);
            SetChildText(toggleObj, "ButtonText", text);

            var toggleComp = toggleObj.GetComponent<Toggle>();
            if (toggleComp != null)
            {
                int id = toggleID;
                toggleComp.onValueChanged.AddListener((isOn) =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUIToggleChange{id} fired (isOn={isOn}).");
                    OnUIToggleChange[id]?.Invoke(isOn);
                });
            }
            else
            {
                MelonLogger.Warning($"UIButtonAPI: MakeToggle({toggleID}) – no Toggle component on prefab.");
            }

            MelonLogger.Msg($"UIButtonAPI: MakeToggle created (ID={toggleID}, text=\"{text}\")");
            return toggleObj;
        }

        /// <summary>
        /// Instantiates a Desktop_UISliderPrefab inside the given row.
        /// Sets the ButtonText label, clamps the slider between min and max,
        /// and registers OnUISliderChange[sliderID].
        /// The event passes the current float value every time the slider moves.
        ///
        /// Example:
        ///   var row = MakeRow(0f, 0f);
        ///   MakeSlider(row, "Speed", 1, 0f, 10f);
        ///   OnUISliderChange[1] += val => playerSpeed = val;
        /// </summary>
        public static GameObject MakeSlider(GameObject row, string text, int sliderID, float min, float max)
        {
            if (row == null || _sliderPrefab == null)
            {
                MelonLogger.Warning($"UIButtonAPI: MakeSlider({sliderID}) – null row or missing prefab.");
                return null;
            }

            if (!OnUISliderChange.ContainsKey(sliderID))
                OnUISliderChange[sliderID] = new UnityEvent<float>();

            GameObject sliderObj = GameObject.Instantiate(_sliderPrefab, row.transform);
            SetZero(sliderObj.transform);
            SetChildText(sliderObj, "ButtonText", text);

            var sliderComp = sliderObj.GetComponent<Slider>();
            if (sliderComp != null)
            {
                sliderComp.minValue = min;
                sliderComp.maxValue = max;

                int id = sliderID;
                sliderComp.onValueChanged.AddListener((val) =>
                {
                    MelonLogger.Msg($"UIButtonAPI: OnUISliderChange{id} fired (value={val}).");
                    OnUISliderChange[id]?.Invoke(val);
                });
            }
            else
            {
                MelonLogger.Warning($"UIButtonAPI: MakeSlider({sliderID}) – no Slider component on prefab.");
            }

            MelonLogger.Msg($"UIButtonAPI: MakeSlider created (ID={sliderID}, text=\"{text}\", min={min}, max={max})");
            return sliderObj;
        }

        #endregion

        // ──────────────────────────────────────────────────────────────
        #region Helpers

        /// <summary>Sets only the Z component of localPosition to 0.</summary>
        private static void SetZero(Transform t)
        {
            var lp = t.localPosition;
            lp.z = 0f;
            t.localPosition = lp;
        }

        /// <summary>Finds a named child and sets its TextMeshProUGUI text.</summary>
        private static void SetChildText(GameObject parent, string childName, string text)
        {
            Transform t = parent.transform.Find(childName);
            if (t == null)
            {
                MelonLogger.Warning($"UIButtonAPI: Child '{childName}' not found on {parent.name}.");
                return;
            }
            var tmp = t.GetComponent<TextMeshProUGUI>();
            if (tmp != null)
                tmp.text = text;
            else
                MelonLogger.Warning($"UIButtonAPI: '{childName}' has no TextMeshProUGUI.");
        }

        #endregion
    }
}