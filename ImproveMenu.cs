using System;
using System.Collections;
using BepInEx.Configuration;
using MenuLib;
using MenuLib.MonoBehaviors;
using TMPro;
using UnityEngine;

#pragma warning disable CS8618

namespace Improve;

public static class ImproveMenu
{
    private static REPOPopupPage _statsPage;
    private static REPOPopupPage _skillsPage;
    private static REPOLabel _availablePointsLabel;
    private static REPOLabel _spentPointsLabel;
    private static REPOSlider[] _skillSliders;

    internal static void Initialize()
    {
        MenuAPI.AddElementToMainMenu(parent => AddImproveButton(parent));
        MenuAPI.AddElementToEscapeMenu(parent => AddImproveButton(parent));
        MenuAPI.AddElementToLobbyMenu(parent => AddImproveButton(parent));
    }

    private static void AddImproveButton(Transform parent)
    {
        // keep your original MenuLib position for now
        var btn = MenuAPI.CreateREPOButton(
            "Improve",
            OpenImproveMenu,
            parent,
            new Vector2(0, 300)
        );

        // fix text alignment AFTER MenuLib finishes layout
        Improve.Instance.StartCoroutine(FixRepoButton(btn, parent));
    }

    private static IEnumerator FixRepoButton(REPOButton btn, Transform parent)
    {
        yield return null;
        Canvas.ForceUpdateCanvases();
    
        // FIX TEXT CANVAS MISALIGNMENT
        var tmp = btn.GetComponentInChildren<TextMeshProUGUI>(true);
        if (tmp != null)
        {
            var btnRt = btn.rectTransform;
            var textRt = (RectTransform)tmp.transform;
    
            textRt.SetParent(btnRt, false);
    
            textRt.anchorMin = Vector2.zero;
            textRt.anchorMax = Vector2.one;
            textRt.pivot = new Vector2(0.5f, 0.5f);
    
            textRt.offsetMin = new Vector2(14, 6);
            textRt.offsetMax = new Vector2(-14, -6);
    
            tmp.alignment = TextAlignmentOptions.Right | TextAlignmentOptions.Midline;
            tmp.enableWordWrapping = false;
        }
    
        // OPTIONAL: move button to true bottom-right
        var parentRt = parent as RectTransform;
        if (parentRt != null)
        {
            float marginRight = 40f;
            float marginBottom = 20f;
    
            float x =
                parentRt.rect.width
                - btn.rectTransform.rect.width
                - marginRight;
    
            float y = marginBottom;
    
            btn.rectTransform.localPosition = new Vector3(x, y, 0);
        }
    }

    private static void OpenImproveMenu()
    {
        Improve.Instance.Config.Reload();
        CreateStatsPage();
        CreateSkillsPage();
    }

    private static void CreateStatsPage()
    {
        _statsPage = MenuAPI.CreateREPOPopupPage("Improve - Progress", REPOPopupPage.PresetSide.Left, false, true);

        int level = SaveData.CurrentLevel();
        int available = SaveData.AvailablePoints();
        int spent = SaveData.TotalSpent();
        int needed = SaveData.HaulNeededForNextLevel();
        string diffLabel = GetDifficultyLabel();
        string availableText = SaveData.IsOverspent() ? $"{available} (OVER!)" : $"{available}";

        // Shrink the text by decreasing font sizes. MenuLib fonts are chunky by default.
        float fontScale = 0.75f;

        // Create labels with tighter vertical spacing
        REPOElement[] elements =
        {
            MenuAPI.CreateREPOLabel("Lifetime Haul:", _statsPage.transform, new Vector2(40, 280)),
            MenuAPI.CreateREPOLabel("Current Level:", _statsPage.transform, new Vector2(40, 255)),
            MenuAPI.CreateREPOLabel("Available Points:", _statsPage.transform, new Vector2(40, 230)),
            MenuAPI.CreateREPOLabel("Spent Points:", _statsPage.transform, new Vector2(40, 205)),
            MenuAPI.CreateREPOLabel("Next Level In:", _statsPage.transform, new Vector2(40, 180)),
            MenuAPI.CreateREPOLabel("Difficulty:", _statsPage.transform, new Vector2(40, 155)),

            MenuAPI.CreateREPOLabel(FormatHaul(SaveData.LifetimeHaul.Value), _statsPage.transform, new Vector2(240, 280)),
            MenuAPI.CreateREPOLabel($"{level}", _statsPage.transform, new Vector2(240, 255)),
            _availablePointsLabel = MenuAPI.CreateREPOLabel(availableText, _statsPage.transform, new Vector2(240, 230)),
            _spentPointsLabel = MenuAPI.CreateREPOLabel($"{spent}", _statsPage.transform, new Vector2(240, 205)),
            MenuAPI.CreateREPOLabel(FormatHaul(needed), _statsPage.transform, new Vector2(240, 180)),
            MenuAPI.CreateREPOLabel(diffLabel, _statsPage.transform, new Vector2(240, 155)),

            // Fix button placements to fit Left panel width (approx 400px usually)
            MenuAPI.CreateREPOButton("Reset Stats", () => MenuAPI.OpenPopup("Reset Stats", Color.yellow,
                "Reclaim all spent stat points? Your haul and level will be preserved.",
                () =>
                {
                    SaveData.ResetStats();
                    _statsPage.ClosePage(true);
                }), _statsPage.transform, new Vector2(40, 80)),

            MenuAPI.CreateREPOButton("Reset All", () => MenuAPI.OpenPopup("Reset All", Color.red,
                "Wipe ALL progress including haul, level, and stat allocations? This cannot be undone!",
                () =>
                {
                    SaveData.ResetAll();
                    _statsPage.ClosePage(true);
                }), _statsPage.transform, new Vector2(220, 80)),

            MenuAPI.CreateREPOButton("Close", () => _statsPage.ClosePage(true), _statsPage.transform,
                new Vector2(130, 20))
        };

        foreach (var element in elements)
        {
            // Apply scale to make fonts smaller / less "wide"
            if (element is REPOLabel lbl && lbl.labelTMP != null)
                lbl.labelTMP.fontSize *= fontScale;
            else if (element is REPOButton btn && btn.labelTMP != null)
            {
                btn.labelTMP.fontSize *= fontScale;
                // Also shrink button width slightly to prevent overlap
                btn.rectTransform.sizeDelta = new Vector2(160, btn.rectTransform.sizeDelta.y);
            }

            _statsPage.AddElement(element.rectTransform, element.rectTransform.localPosition);
        }

        _statsPage.OpenPage(false);
    }

    private static void CreateSkillsPage()
    {
        int available = SaveData.AvailablePoints();
        int spendable = Math.Max(available, 0); // Can't spend if overspent

        _skillsPage = MenuAPI.CreateREPOPopupPage("Improve - Skills", REPOPopupPage.PresetSide.Right, false, false);
        
        float fontScale = 0.75f;

        // Show warning if overspent (difficulty was raised)
        if (SaveData.IsOverspent())
        {
            var warning = MenuAPI.CreateREPOLabel(
                $"Over budget by {Math.Abs(available)} pts! Reset stats or earn more haul.",
                _skillsPage.transform, default);
            if (warning.labelTMP != null) warning.labelTMP.fontSize *= fontScale;
            _skillsPage.AddElementToScrollView(warning.rectTransform);
        }

        _skillSliders = new[]
        {
            CreateSlider("Health", SaveData.AllocHealth, spendable, 0),
            CreateSlider("Sprint Speed", SaveData.AllocSpeed, spendable, 1),
            CreateSlider("Stamina", SaveData.AllocStamina, spendable, 2),
            CreateSlider("Extra Jump", SaveData.AllocExtraJump, spendable, 3),
            CreateSlider("Grab Range", SaveData.AllocGrabRange, spendable, 4),
            CreateSlider("Grab Strength", SaveData.AllocGrabStrength, spendable, 5),
            CreateSlider("Throw", SaveData.AllocGrabThrow, spendable, 6),
            CreateSlider("Tumble Launch", SaveData.AllocTumbleLaunch, spendable, 7),
            CreateSlider("Tumble Climb", SaveData.AllocTumbleClimb, spendable, 8),
            CreateSlider("Tumble Wings", SaveData.AllocTumbleWings, spendable, 9),
            CreateSlider("Crouch Rest", SaveData.AllocCrouchRest, spendable, 10),
            CreateSlider("Map Count", SaveData.AllocMapPlayerCount, spendable, 11, maxCap: 1),
            CreateSlider("Death Head", SaveData.AllocDeathHeadBattery, spendable, 12)
        };

        foreach (var slider in _skillSliders)
        {
            if (slider.labelTMP != null) slider.labelTMP.fontSize *= fontScale;
            // Removed minTMP, maxTMP, valueTMP scaling as REPOSlider does not expose them directly
            _skillsPage.AddElementToScrollView(slider.rectTransform);
        }

        _skillSliders[0].repoScrollViewElement.topPadding = 5;
        _skillsPage.scrollView.spacing = 5;

        _skillsPage.OpenPage(true);
    }

    private static REPOSlider CreateSlider(string name, ConfigEntry<int> entry, int available, int index, int maxCap = int.MaxValue)
    {
        return MenuAPI.CreateREPOSlider(name,
            string.Empty,
            value => OnPointChanged(index, entry, value),
            _skillsPage.transform,
            default, 0,
            Math.Clamp(entry.Value + available, 0, maxCap),
            entry.Value);
    }

    private static void OnPointChanged(int index, ConfigEntry<int> entry, int value)
    {
        entry.Value = value;

        int available = SaveData.AvailablePoints();
        int spendable = Math.Max(available, 0);
        string availableText = SaveData.IsOverspent() ? $"{available} (OVER!)" : $"{available}";
        _availablePointsLabel.labelTMP.text = availableText;
        _spentPointsLabel.labelTMP.text = $"{SaveData.TotalSpent()}";

        for (int i = 0; i < _skillSliders.Length; i++)
        {
            var slider = _skillSliders[i];
            int limit = slider.labelTMP.text == "Map Count" ? 1 : int.MaxValue;

            if (i == index)
                slider.max = Mathf.Min(value + spendable, limit);
            else
                slider.max = Mathf.Min(slider.value + spendable, limit);

            slider.SetValue(slider.value, false);
        }
    }

    private static string GetDifficultyLabel()
    {
        float diff = Improve.DifficultyMultiplier.Value;
        if (diff <= 0.25f) return "Easy";
        if (diff <= 0.5f) return "Standard";
        if (diff <= 0.75f) return "Hard";
        return "Hardest";
    }

    private static string FormatHaul(int value)
    {
        // Always express haul in thousands (K) rather than collapsing big values to "M".
        // On 30K–200K maps an "M" display rounds the gain away (e.g. +35K is invisible at "15M");
        // in K the same gain reads clearly: 1,000,000 → "1000K", and finishing a 35K map shows
        // 15000K → 15035K. Display only, floored to whole K — LifetimeHaul keeps full precision.
        return (value / 1_000) + "K";
    }
}
