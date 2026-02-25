using System;
using BepInEx.Configuration;
using MenuLib;
using MenuLib.MonoBehaviors;
using UnityEngine;

#pragma warning disable CS8618

namespace Improve;

public static class ImproveMenu
{
    private static REPOPopupPage _statsPage;
    private static REPOPopupPage _skillsPage;
    private static REPOLabel _availablePointsLabel;
    private static REPOSlider[] _skillSliders;

    internal static void Initialize()
    {
        // Place button after Mods button — uses Y offset lower than default
        MenuAPI.AddElementToMainMenu(parent =>
            MenuAPI.CreateREPOButton("Improve", OpenImproveMenu, parent, new Vector2(0, 300)));
        MenuAPI.AddElementToEscapeMenu(parent =>
            MenuAPI.CreateREPOButton("Improve", OpenImproveMenu, parent, new Vector2(0, 300)));
        MenuAPI.AddElementToLobbyMenu(parent =>
            MenuAPI.CreateREPOButton("Improve", OpenImproveMenu, parent, new Vector2(0, 300)));
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

        REPOElement[] elements =
        {
            MenuAPI.CreateREPOLabel("Lifetime Haul:", _statsPage.transform, new Vector2(70, 280)),
            MenuAPI.CreateREPOLabel("Current Level:", _statsPage.transform, new Vector2(70, 255)),
            MenuAPI.CreateREPOLabel("Available Points:", _statsPage.transform, new Vector2(70, 230)),
            MenuAPI.CreateREPOLabel("Spent Points:", _statsPage.transform, new Vector2(70, 205)),
            MenuAPI.CreateREPOLabel("Next Level In:", _statsPage.transform, new Vector2(70, 180)),
            MenuAPI.CreateREPOLabel("Difficulty:", _statsPage.transform, new Vector2(70, 155)),

            MenuAPI.CreateREPOLabel($"{SaveData.LifetimeHaul.Value:N0}", _statsPage.transform, new Vector2(260, 280)),
            MenuAPI.CreateREPOLabel($"{level}", _statsPage.transform, new Vector2(260, 255)),
            _availablePointsLabel = MenuAPI.CreateREPOLabel(availableText, _statsPage.transform, new Vector2(260, 230)),
            MenuAPI.CreateREPOLabel($"{spent}", _statsPage.transform, new Vector2(260, 205)),
            MenuAPI.CreateREPOLabel($"{needed:N0}", _statsPage.transform, new Vector2(260, 180)),
            MenuAPI.CreateREPOLabel(diffLabel, _statsPage.transform, new Vector2(260, 155)),

            // Reset Stats button — keeps haul
            MenuAPI.CreateREPOButton("Reset Stats", () => MenuAPI.OpenPopup("Reset Stats", Color.yellow,
                "Reclaim all spent stat points? Your haul and level will be preserved.",
                () =>
                {
                    SaveData.ResetStats();
                    _statsPage.ClosePage(true);
                }), _statsPage.transform, new Vector2(60, 20)),

            // Reset All button — wipes everything
            MenuAPI.CreateREPOButton("Reset All", () => MenuAPI.OpenPopup("Reset All", Color.red,
                "Wipe ALL progress including haul, level, and stat allocations? This cannot be undone!",
                () =>
                {
                    SaveData.ResetAll();
                    _statsPage.ClosePage(true);
                }), _statsPage.transform, new Vector2(200, 20)),

            MenuAPI.CreateREPOButton("Close", () => _statsPage.ClosePage(true), _statsPage.transform,
                new Vector2(340, 20))
        };

        foreach (var element in elements)
        {
            _statsPage.AddElement(element.rectTransform, element.rectTransform.localPosition);
        }

        _statsPage.OpenPage(false);
    }

    private static void CreateSkillsPage()
    {
        int available = SaveData.AvailablePoints();
        int spendable = Math.Max(available, 0); // Can't spend if overspent

        _skillsPage = MenuAPI.CreateREPOPopupPage("Improve - Skills", REPOPopupPage.PresetSide.Right, false, false);

        // Show warning if overspent (difficulty was raised)
        if (SaveData.IsOverspent())
        {
            var warning = MenuAPI.CreateREPOLabel(
                $"Over budget by {Math.Abs(available)} pts! Reset stats or earn more haul.",
                _skillsPage.transform, default);
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
}
