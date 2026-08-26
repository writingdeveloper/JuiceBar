using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;
using JuiceBar.Core.Localization;

namespace JuiceBar.Ui;

/// <summary>
/// 언어가 바뀌면 화면의 글월도 곧바로 따라 바뀌게 하는 다리.
///
/// 마크업 확장이 값을 한 번 돌려주고 끝내면 창을 다시 열어야 새 언어가 보인다.
/// 그래서 값 대신 이 객체의 인덱서를 가리키는 바인딩을 돌려주고,
/// 언어가 바뀔 때 "전부 다시 읽어라" 는 신호를 보낸다.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private TranslationSource()
        => Loc.Changed += (_, _) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// XAML 에서 <c>{ui:Translate popup.today}</c> 처럼 쓴다.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TranslateExtension : MarkupExtension
{
    public TranslateExtension() { }

    public TranslateExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
        => new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay,
        }.ProvideValue(serviceProvider);
}
