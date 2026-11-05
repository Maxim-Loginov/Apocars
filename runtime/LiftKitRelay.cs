using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Apocars
{
    /// On a car's lift kit slot: the car's checkSuspension FSM switches the four wheels the game's cars have
    /// (hinge_wheel_FL, FR, RL, RR) to their lifted or stock suspension; this switches any more wheels the car has
    /// (hinge_wheel_RL_2, ...) along with them.
    public class LiftKitRelay : MonoBehaviour
    {
        private static readonly HashSet<string> GameWheels = new HashSet<string> { "hinge_wheel_FL", "hinge_wheel_FR", "hinge_wheel_RL", "hinge_wheel_RR" };
        private int _fitted = -1;   // unknown at first: sent once the FSMs are running
        private float _next;

        private void Update()
        {
            if (Time.time < _next) return;
            _next = Time.time + 0.2f;
            int fitted = transform.childCount > 0 ? 1 : 0;
            if (fitted == _fitted) return;
            var extra = transform.root.GetComponentsInChildren<PlayMakerFSM>(true)
                .Where(f => f.FsmName == "Suspension" && f.gameObject.activeInHierarchy && !GameWheels.Contains(f.gameObject.name)).ToList();
            if (extra.Any(f => f.Fsm == null || !f.Fsm.Initialized)) return;   // not started yet: next time
            foreach (var f in extra) f.SendEvent(fitted == 1 ? "Activate" : "Deactivate");
            _fitted = fitted;
        }
    }
}
