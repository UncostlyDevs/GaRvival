using System;
using JunkWars.Client;
using JunkWars.Client.Garage;
using UnityEngine;

namespace GarRevival
{
    // The arcade machine in the garage holds a complete shooting-gallery game
    // (ArcadeGame_RobotShooter, "Alienoids"). The original only showed it
    // while you waited in the matchmaking queue: the camera moved to the
    // machine and you played on its screen until a match was found. With no
    // queue left, clicking the machine now does that camera move, hides the
    // garage interface, and this overlay explains the controls and offers the
    // way back.
    internal sealed class ArcadeMode : MonoBehaviour
    {
        public static bool Active { get; private set; }

        private static float _queueHiddenUntil;
        private static GameObject _queueUi;   // the old matchmaking queue screen

        private static void FindUi()
        {
            if (_queueUi == null)
            {
                _queueUi = GameObject.Find("UI Root - Garage/BattleQueueHolder");
            }
        }

        private static void HideQueueUi()
        {
            FindUi();
            if (_queueUi != null && _queueUi.activeSelf)
            {
                _queueUi.SetActive(false);
            }
        }
        private static GUIStyle _box, _text, _button;

        // Hook on AssemblyMouseManager.SelectGrageInteractiveObject (a click
        // in the garage). True = handled here.
        public static bool OnGarageClick(ref bool result)
        {
            Camera camera = Camera.main;
            RaycastHit hit;
            if (Active || camera == null || !Physics.Raycast(camera.ScreenPointToRay(Input.mousePosition), out hit) ||
                hit.collider.gameObject.tag != "IntractiveGarageObject" || hit.collider.gameObject.GetComponent<ArcadeMachine>() == null)
            {
                return false; // the radio and everything else: the game's own handling
            }
            Enter();
            result = true;
            return true;
        }

        public static void Enter()
        {
            GarageManager garage = GameManager.Instance.garage;
            if (Active || garage == null || garage.inTranssition || GameManager.Instance.currentGameState != GameState.Garage)
            {
                return;
            }
            Active = true;
            RevivalLog.Info("Arcade: playing (garage state " + garage.garageState + ")");
            FindUi();
            // As joining the matchmaking queue did: the garage's own screens
            // put themselves away (and come back on "left").
            Messenger<Message>.Invoke(DataManager.NETWORK_USER_JOINED_LOBBY, new Message(DataManager.NETWORK_USER_JOINED_LOBBY, null));
            garage.DoRobotAssembyToBattleQueueTransition();
        }

        public static void Leave()
        {
            if (!Active)
            {
                return;
            }
            Active = false;
            RevivalLog.Info("Arcade: back to the garage");
            CustomCursorManager.ShowCursor(newValue: true); // the game hides it over the arcade screen
            _queueHiddenUntil = Time.realtimeSinceStartup + 5f;
            if (GameManager.Instance.currentGameState == GameState.Garage)
            {
                // As leaving the queue did: the garage moves the camera back
                // (GarageManager.OnLeftQueue) and its screens return.
                Messenger<Message>.Invoke(DataManager.NETWORK_USER_LEFT_LOBBY, new Message(DataManager.NETWORK_USER_LEFT_LOBBY, null));
            }
        }

        private void Update()
        {
            if (Active && GameManager.Instance.currentGameState != GameState.Garage)
            {
                // A hosted battle started, say: the garage is gone.
                Active = false;
            }
            if (Active || Time.realtimeSinceStartup < _queueHiddenUntil)
            {
                HideQueueUi();
            }
        }

        private void OnGUI()
        {
            if (!Active)
            {
                return;
            }
            if (_box == null)
            {
                _box = new GUIStyle(GUI.skin.box);
                _text = new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleLeft, wordWrap = true };
                _text.normal.textColor = Color.white;
                _button = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold };
            }
            float w = Mathf.Min(720f, Screen.width - 32f);
            Rect panel = new Rect((Screen.width - w) / 2f, Screen.height - 96f, w, 80f);
            GUI.Box(panel, GUIContent.none, _box);
            GUI.Box(panel, GUIContent.none, _box); // twice: darker
            GUI.Label(new Rect(panel.x + 16f, panel.y + 8f, w - 220f, 64f),
                "ALIENOIDS  -  Click the screen to start or resume.  Left click: shoot.  Right click: reload.  Esc: pause.", _text);
            if (GUI.Button(new Rect(panel.xMax - 196f, panel.y + 16f, 180f, 48f), "LEAVE ARCADE", _button))
            {
                Leave();
            }
        }
    }
}
