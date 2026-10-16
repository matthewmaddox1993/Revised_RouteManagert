using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;

namespace RevisedDVRoute
{
    public class Settings : UnityModManager.ModSettings, IDrawable
    {
        [Header("Routing")]
        [Draw(DrawType.ToggleGroup, Label = "Reversing strategy")] public ReversingStrategy ReversingStrategy = ReversingStrategy.OnlyIfNeeded;
        [Header("Compatibility")]
        [Draw("Respect DV Signals reservations", DrawType.Toggle)] public bool RespectDVSignalsReservations = true;
        // Off by default: DV Signals turns every signal sharing a reserved block red,
        // including the one in front of the player.
        [Draw("Reserve current DV Signals block for active route (turns shared signals red)", DrawType.Toggle)] public bool ReserveDVSignalsBlock = false;
        [Header("Keys")]
[Draw(DrawType.KeyBinding)] public KeyBinding TrainEndAlarm = new KeyBinding() { keyCode = KeyCode.N };
        public void OnChange()
        {
            if (!RespectDVSignalsReservations || !ReserveDVSignalsBlock)
                Compatibility.DVSignalsCompatibility.ClearPlayerRouteReservation();
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
