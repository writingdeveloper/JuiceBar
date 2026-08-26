namespace JuiceBar.Tests;

/// <summary>
/// 현재 언어는 프로세스 전역 상태다. 언어를 바꾸는 테스트끼리 나란히 돌면
/// 서로의 설정을 덮어써서 되는 날도 있고 안 되는 날도 있는 테스트가 된다.
/// 같은 컬렉션에 묶어 차례로 돌게 한다.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LocalizationCollection
{
    public const string Name = "Localization";
}
