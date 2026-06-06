namespace Game
{
    /// <summary>プレイヤーの所持品スロットを管理する。</summary>
    public class Inventory
    {
        public int slots;

        public void Add(int item) { slots += item; }
    }
}
