namespace Game
{
    /// <summary>Addressables からプレイヤープレハブをロードする。</summary>
    public class AddressableLoader
    {
        public void Load()
        {
            Addressables.LoadAssetAsync<object>("player-prefab");
        }
    }
}
