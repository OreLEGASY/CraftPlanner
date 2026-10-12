using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftPlanner.App.Services;
using CraftPlanner.Core.Models;

namespace CraftPlanner.App.ViewModels;

/// <summary>メインウィンドウの状態。ゲームデータの選択、アイテム一覧、開いている計画タブ、保存状態を持つ。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    public MainViewModel(AppServices services)
    {
        Services = services;
        Games = new ObservableCollection<GameData>(services.LoadedGames.OrderBy(g => g.Name, StringComparer.CurrentCulture));
        ItemList = new ItemListViewModel();
        services.SaveStatusChanged += (msg, isError) =>
        {
            StatusText = isError ? msg : $"{msg}（{DateTime.Now:HH:mm:ss}）";
            StatusIsError = isError;
        };
        StatusText = $"データフォルダー: {services.Paths.Root}";

        var last = services.Settings.LastGameDataId;
        SelectedGame = Games.FirstOrDefault(g => g.Id == last) ?? Games.FirstOrDefault();
    }

    public AppServices Services { get; }
    public ObservableCollection<GameData> Games { get; }
    public ItemListViewModel ItemList { get; }
    public ObservableCollection<PlanViewModel> OpenPlans { get; } = new();

    [ObservableProperty] private GameData? _selectedGame;
    [ObservableProperty] private PlanViewModel? _activePlan;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsError;

    public bool HasUnsavedChanges => OpenPlans.Any(p => p.IsDirty);

    partial void OnSelectedGameChanged(GameData? value)
    {
        ItemList.Game = value;
        if (value != null && Services.Settings.LastGameDataId != value.Id)
        {
            Services.Settings.LastGameDataId = value.Id;
            Services.SaveSettings();
        }
    }

    partial void OnActivePlanChanged(PlanViewModel? value)
    {
        PersistTabs();
        // アクティブな計画のゲームデータをアイテム一覧にも反映する
        if (value != null && SelectedGame?.Id != value.Game.Id)
            SelectedGame = Games.FirstOrDefault(g => g.Id == value.Game.Id) ?? SelectedGame;
    }

    public IEnumerable<Plan> PlansOfSelectedGame
        => SelectedGame == null ? Enumerable.Empty<Plan>() : Services.PlansOf(SelectedGame.Id).OrderBy(p => p.Name, StringComparer.CurrentCulture);

    // ------------------------------------------------------------------
    // タブ
    // ------------------------------------------------------------------

    public void RestoreTabs()
    {
        foreach (var id in Services.Settings.OpenPlanIds.ToList())
        {
            var plan = Services.LoadedPlans.FirstOrDefault(p => p.Id == id);
            if (plan == null) continue;
            OpenPlan(plan, activate: false);
        }
        var active = OpenPlans.FirstOrDefault(p => p.Plan.Id == Services.Settings.ActivePlanId) ?? OpenPlans.FirstOrDefault();
        ActivePlan = active;
    }

    public PlanViewModel? OpenPlan(Plan plan, bool activate = true)
    {
        var existing = OpenPlans.FirstOrDefault(p => p.Plan.Id == plan.Id);
        if (existing != null)
        {
            if (activate) ActivePlan = existing;
            return existing;
        }
        var game = Services.LoadedGames.FirstOrDefault(g => g.Id == plan.GameDataId);
        if (game == null)
        {
            StatusText = $"計画「{plan.Name}」の参照元ゲームデータ「{plan.GameDataId}」が見つかりません。";
            StatusIsError = true;
            return null;
        }
        var vm = new PlanViewModel(Services, plan, game);
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlanViewModel.IsDirty)) OnPropertyChanged(nameof(HasUnsavedChanges)); };
        OpenPlans.Add(vm);
        if (activate) ActivePlan = vm;
        PersistTabs();
        return vm;
    }

    /// <summary>タブを閉じる（計画は削除しない）。閉じる前に保存する。保存に失敗したら閉じない。</summary>
    public bool ClosePlan(PlanViewModel vm)
    {
        if (vm.IsDirty && !vm.SaveNow()) return false;
        vm.Close();
        var index = OpenPlans.IndexOf(vm);
        OpenPlans.Remove(vm);
        if (ActivePlan == vm) ActivePlan = OpenPlans.Count == 0 ? null : OpenPlans[Math.Clamp(index, 0, OpenPlans.Count - 1)];
        PersistTabs();
        return true;
    }

    public void PersistTabs()
    {
        Services.Settings.OpenPlanIds = OpenPlans.Select(p => p.Plan.Id).ToList();
        Services.Settings.ActivePlanId = ActivePlan?.Plan.Id;
        Services.SaveSettings();
    }

    public bool SaveAll()
    {
        var ok = true;
        foreach (var p in OpenPlans) ok &= p.SaveNow();
        if (SelectedGame != null) ok &= Services.SaveGame(SelectedGame);
        return ok;
    }

    // ------------------------------------------------------------------
    // ゲームデータの変更通知
    // ------------------------------------------------------------------

    /// <summary>アイテム・レシピを編集した後に呼ぶ。保存し、一覧と開いている計画を更新する。</summary>
    public bool GameDataChanged(GameData game)
    {
        var ok = Services.SaveGame(game);
        ItemList.Refresh();
        foreach (var p in OpenPlans.Where(p => p.Game.Id == game.Id)) p.Refresh();
        return ok;
    }

    /// <summary>ゲームデータのインスタンスが差し替わった（取り込み・復元）ときに、一覧とタブを作り直す。</summary>
    public void GameReplaced(GameData game)
    {
        var idx = Games.ToList().FindIndex(g => g.Id == game.Id);
        if (idx >= 0) Games[idx] = game; else Games.Add(game);
        if (SelectedGame?.Id == game.Id) SelectedGame = game;
        ItemList.Game = game;
        ItemList.Refresh();
        foreach (var vm in OpenPlans.Where(p => p.Game.Id == game.Id).ToList())
        {
            var wasActive = ActivePlan == vm;
            var index = OpenPlans.IndexOf(vm);
            vm.Close();
            OpenPlans.Remove(vm);
            var plan = Services.LoadedPlans.FirstOrDefault(p => p.Id == vm.Plan.Id);
            if (plan == null) continue;
            var fresh = new PlanViewModel(Services, plan, game);
            OpenPlans.Insert(index, fresh);
            if (wasActive) ActivePlan = fresh;
        }
    }

    /// <summary>計画のインスタンスが差し替わった（復元）ときにタブを作り直す。</summary>
    public void PlanReplaced(Plan plan)
    {
        var vm = OpenPlans.FirstOrDefault(p => p.Plan.Id == plan.Id);
        if (vm == null) return;
        var wasActive = ActivePlan == vm;
        var index = OpenPlans.IndexOf(vm);
        vm.Close();
        OpenPlans.Remove(vm);
        var game = Services.LoadedGames.FirstOrDefault(g => g.Id == plan.GameDataId);
        if (game == null) return;
        var fresh = new PlanViewModel(Services, plan, game);
        OpenPlans.Insert(index, fresh);
        if (wasActive) ActivePlan = fresh;
    }

    public void RefreshGames()
    {
        var selectedId = SelectedGame?.Id;
        Games.Clear();
        foreach (var g in Services.LoadedGames.OrderBy(g => g.Name, StringComparer.CurrentCulture)) Games.Add(g);
        SelectedGame = Games.FirstOrDefault(g => g.Id == selectedId) ?? Games.FirstOrDefault();
    }
}
