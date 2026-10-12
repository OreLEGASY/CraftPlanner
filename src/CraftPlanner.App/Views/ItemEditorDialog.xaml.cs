using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CraftPlanner.App.Services;
using CraftPlanner.App.ViewModels;
using CraftPlanner.Core.Data;
using CraftPlanner.Core.Models;
using Microsoft.Win32;

namespace CraftPlanner.App.Views;

/// <summary>レシピ編集用の下書き。OK 時にまとめてゲームデータへ反映する。</summary>
public sealed partial class RecipeDraft : ObservableObject
{
    public string? Id { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _outputText = "1";
    public ObservableCollection<MaterialDraft> Materials { get; } = new();
    public string Header
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name) ? "(名称なし)" : Name;
            var mats = Materials.Count == 0 ? "素材なし" : string.Join(" + ", Materials.Select(m => $"{m.Name}×{m.Quantity}"));
            return $"{name}  ×{OutputText}  ← {mats}";
        }
    }
    public void RaiseHeader() => OnPropertyChanged(nameof(Header));
}

public sealed partial class MaterialDraft : ObservableObject
{
    public string ItemId { get; init; } = "";
    public string Name { get; init; } = "";
    [ObservableProperty] private string _quantity = "1";
}

/// <summary>
/// アイテムとそのレシピをまとめて編集する。入力中はゲームデータを触らず、OK で
/// ゲームデータの複製に対して適用 → 検証に通ったら本体へ反映 → 保存、の順で行う（途中失敗で半端な状態を残さない）。
/// </summary>
public partial class ItemEditorDialog : Window
{
    private readonly MainViewModel _main;
    private readonly GameData _game;
    private readonly Item? _existing;
    private string? _iconPng;
    private readonly ObservableCollection<RecipeDraft> _recipes = new();
    private RecipeDraft? _current;
    private bool _loadingRecipe;

    public Item? Result { get; private set; }

    private ItemEditorDialog(MainViewModel main, GameData game, Item? existing, string? initialName)
    {
        InitializeComponent();
        _main = main;
        _game = game;
        _existing = existing;
        RecipeList.ItemsSource = _recipes;

        if (existing != null)
        {
            Title = $"アイテムの編集 - {existing.Name}";
            NameBox.Text = existing.Name;
            IdBox.Text = existing.Id;
            IdBox.IsReadOnly = true;
            IdBox.Background = SystemColors.ControlBrush;
            SummaryBox.Text = existing.Summary;
            DescriptionBox.Text = existing.Description;
            AcquisitionBox.Text = existing.Acquisition;
            _iconPng = existing.IconPng;
            foreach (var r in existing.Recipes)
            {
                var d = new RecipeDraft { Id = r.Id, Name = r.Name, OutputText = r.OutputQuantity.ToString() };
                foreach (var m in r.Materials)
                    d.Materials.Add(new MaterialDraft { ItemId = m.ItemId, Name = game.FindItem(m.ItemId)?.Name ?? $"(不明: {m.ItemId})", Quantity = m.Quantity.ToString() });
                _recipes.Add(d);
            }
            var usages = game.FindUsages(existing.Id)
                .Select(u => $"・{u.Product.Name} の {(string.IsNullOrWhiteSpace(u.Recipe.Name) ? "レシピ" : u.Recipe.Name)}（×{u.Quantity}）").ToList();
            UsagesList.ItemsSource = usages.Count == 0 ? new List<string> { "（使用先はありません）" } : usages;
        }
        else
        {
            Title = "アイテムの新規登録";
            NameBox.Text = initialName ?? "";
            UsagesList.ItemsSource = new List<string> { "（登録後に表示されます）" };
        }
        RefreshIcon();
        if (_recipes.Count > 0) RecipeList.SelectedIndex = 0;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public static bool EditExisting(Window owner, MainViewModel main, GameData game, Item item)
    {
        var dlg = new ItemEditorDialog(main, game, item, null) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    public static Item? CreateNew(Window owner, MainViewModel main, GameData game, string? initialName)
    {
        var dlg = new ItemEditorDialog(main, game, null, initialName) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg.Result : null;
    }

    // ---- アイコン ----

    private void RefreshIcon() => IconImage.Source = IconService.Decode(_iconPng);

    private void PickIcon_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|すべてのファイル|*.*", Title = "アイコン画像を選択" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _iconPng = IconService.EncodeFromFile(dlg.FileName);
            RefreshIcon();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "画像を読み込めませんでした: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearIcon_Click(object sender, RoutedEventArgs e)
    {
        _iconPng = null;
        RefreshIcon();
    }

    // ---- レシピ ----

    private void AddRecipe_Click(object sender, RoutedEventArgs e)
    {
        var d = new RecipeDraft();
        _recipes.Add(d);
        RecipeList.SelectedItem = d;
        RecipeNameBox.Focus();
    }

    private void RemoveRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (RecipeList.SelectedItem is not RecipeDraft d) return;
        if (MessageBox.Show(this, $"レシピ「{d.Header}」を削除しますか？", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _recipes.Remove(d);
    }

    private void RecipeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = RecipeList.SelectedItem as RecipeDraft;
        _loadingRecipe = true;
        RecipePanel.IsEnabled = _current != null;
        RecipeNameBox.Text = _current?.Name ?? "";
        OutputBox.Text = _current?.OutputText ?? "";
        MaterialList.ItemsSource = _current?.Materials;
        _loadingRecipe = false;
    }

    private void RecipeField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingRecipe || _current == null) return;
        _current.Name = RecipeNameBox.Text;
        _current.OutputText = OutputBox.Text;
        _current.RaiseHeader();
    }

    private void AddMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;
        var picked = ItemPickerDialog.Pick(this, _main, _game, "素材を選択");
        if (picked == null) return;
        var dup = _current.Materials.FirstOrDefault(m => m.ItemId == picked.Id);
        if (dup != null)
        {
            MessageBox.Show(this, $"「{picked.Name}」は既にこのレシピに登録されています。既存の行の数量を編集してください。", Title);
            MaterialList.SelectedItem = dup;
            return;
        }
        _current.Materials.Add(new MaterialDraft { ItemId = picked.Id, Name = picked.Name, Quantity = "1" });
        _current.RaiseHeader();
    }

    private void RemoveMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || sender is not FrameworkElement { DataContext: MaterialDraft m }) return;
        _current.Materials.Remove(m);
        _current.RaiseHeader();
    }

    // ---- 確定 ----

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { MessageBox.Show(this, "名前を入力してください。", Title); NameBox.Focus(); return; }

        // ゲームデータの複製に適用し、全部成功した場合だけ本体へ反映する
        var working = _game.Clone();
        var svc = new GameDataService(working);
        Item target;
        try
        {
            if (_existing == null)
            {
                var id = IdBox.Text.Trim();
                target = svc.CreateItem(name, id.Length == 0 ? null : id);
            }
            else
            {
                target = working.FindItem(_existing.Id) ?? throw new InvalidOperationException("編集中のアイテムが見つかりません（別の操作で削除された可能性があります）。");
            }
            svc.UpdateItem(target.Id, name, SummaryBox.Text, DescriptionBox.Text, AcquisitionBox.Text, _iconPng);

            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in _recipes)
            {
                if (!int.TryParse(d.OutputText.Trim(), out var output) || output < 1)
                    throw new ArgumentException($"レシピ「{(string.IsNullOrWhiteSpace(d.Name) ? "(名称なし)" : d.Name)}」の完成数量は 1 以上の整数にしてください。");
                var mats = new List<RecipeMaterial>();
                foreach (var m in d.Materials)
                {
                    if (!int.TryParse(m.Quantity.Trim(), out var q) || q < 1)
                        throw new ArgumentException($"素材「{m.Name}」の数量は 1 以上の整数にしてください。");
                    mats.Add(new RecipeMaterial { ItemId = m.ItemId, Quantity = q });
                }
                string recipeId;
                if (d.Id == null || working.FindRecipe(d.Id) == null) recipeId = svc.AddRecipe(target.Id, d.Name, output).Id;
                else recipeId = d.Id;
                svc.UpdateRecipe(recipeId, d.Name, output, mats);
                keep.Add(recipeId);
            }
            foreach (var old in target.Recipes.Where(r => !keep.Contains(r.Id)).ToList()) svc.DeleteRecipe(old.Id);

            var v = Validators.ValidateGameData(working);
            if (!v.IsValid) throw new InvalidOperationException(string.Join("\n", v.Errors));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "入力内容を確認してください", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 本体へ反映（対象アイテムだけ差し替える）
        var idx = _game.Items.FindIndex(i => i.Id == target.Id);
        var applied = target.Clone();
        if (idx >= 0) _game.Items[idx] = applied; else _game.Items.Add(applied);
        Result = applied;

        if (!_main.GameDataChanged(_game))
        {
            MessageBox.Show(this, "変更は反映されましたが保存に失敗しました。ステータスバーのエラーを確認し、Ctrl+S で再保存してください。", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        DialogResult = true;
    }
}
