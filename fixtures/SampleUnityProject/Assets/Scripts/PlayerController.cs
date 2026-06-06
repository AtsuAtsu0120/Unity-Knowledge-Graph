using UnityEngine;

namespace Game
{
    /// <summary>プレイヤーの入力・戦闘・所持品操作をまとめる中心的なコンポーネント。</summary>
    [RequireComponent(typeof(Weapon))]
    public class PlayerController : Character, IDamageable
    {
        [SerializeField]
        public Inventory inventory;

        public void TakeDamage(int amount)
        {
            health -= amount;
        }

        public void Attack()
        {
            var weapon = GetComponent<Weapon>();
            inventory.Add(1);
            TakeDamage(0);
        }
    }
}
