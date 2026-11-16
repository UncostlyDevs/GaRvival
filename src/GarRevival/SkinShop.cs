using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JunkWars.Client;
using JunkWars.Client.Garage;
using JunkWars.Client.Robot;
using JunkWars.Common.Robot.Enums;
using JunkWars.Common.Robot.Templates;
using JunkWars.Common.Robot.ValueObjects;
using UnityEngine;

namespace GarRevival;

// Garage skin screen: with a part selected on a robot, SKINS (or F8) lists every skin with a swatch.
// BUY uses the client's own buy request (confirm popup, op 155) and wears the skin once bought;
// EQUIP and ORIGINAL LOOK send op 154. The client's response handlers swap the model and icon.
public sealed class SkinShop : MonoBehaviour
{
    private sealed class PendingEquip
    {
        public Part Part;
        public byte Index;
        public float Until;
        public bool Reopen;
    }

    private const float PanelWidth = 600f;
    private const float PanelHeight = 560f;
    private const float CardWidth = 182f;
    private const float CardHeight = 124f;
    private const float BuyWait = 120f;

    private static readonly FieldInfo HandleMouse = typeof(AssemblyMouseManager).GetField("handleMouse", BindingFlags.NonPublic | BindingFlags.Instance);
    private static SkinShop _instance;
    private static PendingEquip _pending;

    private readonly Dictionary<byte, Texture2D> _swatches = new();
    private Part _part;
    private GarageState _openedIn;
    private bool _blocked;
    private bool _mouseWasOn;
    private bool _failed;
    private float _scale = 1f;
    private Rect _button, _panel;
    private Vector2 _scroll;

    private GUIStyle _title, _name, _dim, _buttonStyle, _buy, _off;
    private Texture2D _panelTex, _bar, _card, _grey, _greyHover, _green, _greenHover;

    private void Awake() => _instance = this;

    // autorun "skins"
    public static void Toggle()
    {
        if (_instance == null) return;
        if (_instance._part != null)
        {
            _instance.Close();
            return;
        }
        _instance.Open(FocusedPart() ?? DockedParts().FirstOrDefault());
        var part = _instance._part;
        RevivalLog.Info(part == null
            ? "skins: nothing to show (no part on a robot, or the garage is busy)"
            : $"skins: opened for part {part.partID} ({part.valueObject.indent})");
    }

    // skin <indent|first> <index>: buy it if needed (no confirm) and wear it
    // on the first part of that template on a robot.
    public static void Wear(string which, byte index)
    {
        var part = DockedParts().FirstOrDefault(p => which == "first" || p.valueObject.indent == which);
        if (part == null)
        {
            RevivalLog.Info("skin: no " + which + " part on a robot");
            return;
        }
        if (index == 0 || Owns(part.valueObject.indent, index))
        {
            Equip(part, index);
            return;
        }
        PhotonNetwork.UnlockPartSkin(part.valueObject.indent, index, true);
        _pending = new PendingEquip { Part = part, Index = index, Until = Time.realtimeSinceStartup + BuyWait };
    }

    // Hook on AssemblyMouseManager.SelectGrageInteractiveObject: a click on
    // the SKINS button must not also select or deselect a part behind it.
    public static bool SwallowGarageClick()
    {
        if (_instance == null) return false;
        var point = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y) / _instance._scale;
        return (_instance._part != null && _instance._panel.Contains(point)) || _instance._button.Contains(point);
    }

    private static bool Usable =>
        GameManager.Instance != null && GameManager.Instance.currentGameState == GameState.Garage && GameManager.Instance.garage != null &&
        !ArcadeMode.Active && !PlayScreen.Showing && PopUpWindowsManager.Instance != null && !PopUpWindowsManager.Instance.IsActive &&
        DataManager.Instance != null && DataManager.Instance.partSkinsTemplateList != null;

    // Only parts docked in a module: an inventory part has no garage model to swap.
    private static bool Skinnable(Part part) =>
        part != null && part.valueObject != null && part.owningAssembly is ModuleAssembly && part.dockingSate == DockingState.Docked && TemplateOf(part) != null;

    private static PartSkinsTemplate TemplateOf(Part part)
    {
        var list = DataManager.Instance.partSkinsTemplateList;
        return list != null && list.TryGetValue(part.valueObject.indent, out var template) && template != null && template.skins != null ? template : null;
    }

    private static Part FocusedPart()
    {
        var part = GameManager.Instance.garage.currentFocusedDockable as Part;
        return Skinnable(part) ? part : null;
    }

    private static IEnumerable<Part> DockedParts() =>
        PlayerManager.Instance.player.partsCollection.Values.Where(Skinnable).OrderBy(p => p.partID);

    private static bool Owns(string indent, byte index)
    {
        var owned = PlayerManager.Instance.player.valueObject.ownedPartSkins;
        return owned != null && owned.Any(s => s != null && s.BelongsTo(indent) && s.Contains(index));
    }

    private static void Equip(Part part, byte index)
    {
        RevivalLog.Info($"Skins: part {part.partID} ({part.valueObject.indent}) wears skin {index}");
        Messenger<DataMessage>.Invoke(DataManager.USER_SET_PART_SKIN_REQUEST, new DataMessage(new object[] { part.partID, index }, null));
    }

    private void Buy(Part part, byte index)
    {
        // The client's confirm popup is NGUI: close first so it can be clicked.
        Close();
        _pending = new PendingEquip { Part = part, Index = index, Until = Time.realtimeSinceStartup + BuyWait, Reopen = true };
        Messenger<DataMessage>.Invoke(DataManager.USER_BUY_PART_SKIN_REQUEST, new DataMessage(new object[] { part.valueObject.indent, index, true }, null));
    }

    // A bought skin goes on the part it was bought for.
    private void FinishPurchase()
    {
        if (_pending == null) return;
        if (Time.realtimeSinceStartup > _pending.Until || !Skinnable(_pending.Part))
        {
            _pending = null;
            return;
        }
        if (!Owns(_pending.Part.valueObject.indent, _pending.Index) || PopUpWindowsManager.Instance.IsActive) return;
        var done = _pending;
        _pending = null;
        Equip(done.Part, done.Index);
        if (done.Reopen)
        {
            Open(done.Part);
        }
    }

    private void Open(Part part)
    {
        if (part == null || !Usable) return;
        _part = part;
        _openedIn = GameManager.Instance.garage.garageState;
        _scroll = Vector2.zero;
    }

    private void Close()
    {
        _part = null;
        Block(false);
    }

    // While the panel is up, neither the NGUI garage screens nor the 3D
    // garage (picking, dragging, right-click removal) react to the mouse.
    private void Block(bool block)
    {
        if (block == _blocked) return;
        _blocked = block;
        for (int i = 0; i < UICamera.list.size; i++)
        {
            UICamera.list[i].enabled = !block;
        }
        GarageManager garage = GameManager.Instance == null ? null : GameManager.Instance.garage;
        AssemblyMouseManager mouse = garage == null ? null : garage.AssemblyMouseManager;
        if (mouse == null) return;
        if (block)
        {
            _mouseWasOn = HandleMouse == null || (bool)HandleMouse.GetValue(mouse);
            mouse.Disable();
        }
        else if (_mouseWasOn && garage.garageState == _openedIn)
        {
            mouse.Enable();
        }
    }

    private void Update()
    {
        try
        {
            if (_part != null && (!Usable || !Skinnable(_part) || GameManager.Instance.garage.garageState != _openedIn || Input.GetKeyDown(KeyCode.Escape)))
            {
                Close();
            }
            else if (Input.GetKeyDown(KeyCode.F8) && Usable)
            {
                if (_part != null)
                {
                    Close();
                }
                else
                {
                    Open(FocusedPart());
                }
            }
            Block(_part != null);
            if (Usable)
            {
                FinishPurchase();
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void Fail(Exception ex)
    {
        if (!_failed)
        {
            _failed = true;
            RevivalLog.Error("Skin screen failed", ex);
        }
        _part = null;
        Block(false);
    }

    private void OnGUI()
    {
        _button = _panel = new Rect(0f, 0f, 0f, 0f);
        var old = GUI.matrix;
        try
        {
            if (!Usable) return;
            Part part = _part ?? FocusedPart();
            if (part == null) return;
            BuildStyles();
            GUI.depth = -900;
            _scale = Screen.height / 720f;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(_scale, _scale, 1f));
            float width = Screen.width / _scale;
            if (_part == null)
            {
                _button = new Rect(width - 156f, 720f - 210f, 140f, 40f);
                if (GUI.Button(_button, "SKINS (F8)", _buttonStyle))
                {
                    Open(part);
                }
            }
            else
            {
                DrawPanel(width);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            GUI.matrix = old;
        }
    }

    private void DrawPanel(float width)
    {
        var template = TemplateOf(_part);
        if (template == null) return;
        _panel = new Rect(width - PanelWidth - 16f, (720f - PanelHeight) / 2f, PanelWidth, PanelHeight);
        GUI.DrawTexture(_panel, _panelTex);
        GUI.DrawTexture(new Rect(_panel.x, _panel.y, _panel.width, 44f), _bar);
        GUI.Label(new Rect(_panel.x + 16f, _panel.y + 4f, _panel.width - 70f, 36f), "SKINS  -  " + PartName(_part), _title);
        if (GUI.Button(new Rect(_panel.xMax - 40f, _panel.y + 6f, 32f, 32f), "X", _buttonStyle))
        {
            Close();
            return;
        }
        var player = PlayerManager.Instance.player;
        GUI.Label(new Rect(_panel.x + 16f, _panel.y + 50f, _panel.width - 32f, 24f),
            player.credits.ToString("#,0") + " credits   " + player.premiumCredits.ToString("#,0") + " sparks   -   bought skins fit every part of this kind", _dim);

        var skins = template.skins.Where(s => s != null && s.showInShop).Select(s => SkinCatalog.ByIndex(s.index)).Where(d => d != null).ToArray();
        const int columns = 3;
        var view = new Rect(_panel.x + 12f, _panel.y + 80f, _panel.width - 24f, _panel.height - 140f);
        var content = new Rect(0f, 0f, view.width - 20f, Mathf.Ceil(skins.Length / (float)columns) * (CardHeight + 8f));
        _scroll = GUI.BeginScrollView(view, _scroll, content);
        byte wearing = _part.valueObject.skinIndex;
        for (int i = 0; i < skins.Length; i++)
        {
            DrawCard(new Rect(i % columns * (CardWidth + 8f), i / columns * (CardHeight + 8f), CardWidth, CardHeight), skins[i], wearing);
        }
        GUI.EndScrollView();

        var original = new Rect(_panel.x + 16f, _panel.yMax - 52f, 200f, 38f);
        if (GUI.Button(original, "ORIGINAL LOOK", wearing == 0 ? _off : _buttonStyle) && wearing != 0)
        {
            Equip(_part, 0);
        }
        GUI.Label(new Rect(original.xMax + 16f, original.y, _panel.width - original.width - 48f, 38f), "Painting still works on top of a skin.  Esc closes.", _dim);
    }

    private void DrawCard(Rect r, SkinDef def, byte wearing)
    {
        GUI.DrawTexture(r, _card);
        GUI.DrawTexture(new Rect(r.x + 8f, r.y + 8f, 64f, 64f), Swatch(def));
        GUI.Label(new Rect(r.x + 80f, r.y + 6f, r.width - 84f, 24f), def.Name, _name);
        GUI.Label(new Rect(r.x + 80f, r.y + 28f, r.width - 84f, 20f), def.Rarity, _dim);
        bool owned = Owns(_part.valueObject.indent, def.Index);
        string status = wearing == def.Index ? "WEARING" : owned ? "OWNED" : SkinCatalog.PriceText(def);
        GUI.Label(new Rect(r.x + 80f, r.y + 48f, r.width - 84f, 24f), status, _name);
        var action = new Rect(r.x + 8f, r.y + 80f, r.width - 16f, 36f);
        if (wearing == def.Index)
        {
            GUI.Label(action, "EQUIPPED", _off);
        }
        else if (owned ? GUI.Button(action, "EQUIP", _buttonStyle) : GUI.Button(action, "BUY", _buy))
        {
            if (owned)
            {
                Equip(_part, def.Index);
            }
            else
            {
                Buy(_part, def.Index);
            }
        }
    }

    private static string PartName(Part part) =>
        DataManager.Instance.partTemplateList.TryGetValue(part.valueObject.indent, out var template) && template.uiData != null
            ? Localizator.Get(template.uiData.name)
            : part.valueObject.indent;

    private Texture2D Swatch(SkinDef def)
    {
        if (!_swatches.TryGetValue(def.Index, out var swatch) || swatch == null)
        {
            swatch = SkinPatterns.Swatch(def, 64);
            _swatches[def.Index] = swatch;
        }
        return swatch;
    }

    private void BuildStyles()
    {
        if (_title != null && _panelTex != null) return;
        _panelTex = Solid(0.09f, 0.09f, 0.11f, 0.97f);
        _bar = Solid(0.55f, 0.10f, 0.10f, 1f);
        _card = Solid(0.15f, 0.15f, 0.18f, 1f);
        _grey = Solid(0.24f, 0.24f, 0.28f, 1f);
        _greyHover = Solid(0.34f, 0.34f, 0.40f, 1f);
        _green = Solid(0.20f, 0.55f, 0.16f, 1f);
        _greenHover = Solid(0.27f, 0.68f, 0.22f, 1f);
        var white = new Color(0.95f, 0.95f, 0.95f);
        _title = Text(22, Color.white, FontStyle.Bold);
        _name = Text(15, white, FontStyle.Bold);
        _dim = Text(13, new Color(0.72f, 0.72f, 0.76f), FontStyle.Normal);
        _buttonStyle = Button(_grey, _greyHover, white);
        _buy = Button(_green, _greenHover, Color.white);
        _off = Button(_card, _card, new Color(0.55f, 0.55f, 0.6f));
    }

    private static Texture2D Solid(float r, float g, float b, float a)
    {
        var texture = new Texture2D(1, 1) { hideFlags = HideFlags.DontUnloadUnusedAsset };
        texture.SetPixel(0, 0, new Color(r, g, b, a));
        texture.Apply();
        return texture;
    }

    private static GUIStyle Text(int size, Color color, FontStyle weight)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = size, fontStyle = weight, alignment = TextAnchor.MiddleLeft, wordWrap = true };
        style.normal.textColor = color;
        return style;
    }

    private static GUIStyle Button(Texture2D normal, Texture2D hover, Color text)
    {
        var style = new GUIStyle(GUI.skin.button) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.background = style.focused.background = normal;
        style.hover.background = style.active.background = hover;
        style.normal.textColor = style.hover.textColor = style.active.textColor = style.focused.textColor = text;
        return style;
    }
}
