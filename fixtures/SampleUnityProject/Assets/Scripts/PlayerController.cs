using UnityEngine;

namespace Game
{
    public class PlayerController : Character, IDamageable
    {
        [SerializeField]
        public Inventory inventory;

        public void TakeDamage(int amount)
        {
            health -= amount;
        }
    }
}
