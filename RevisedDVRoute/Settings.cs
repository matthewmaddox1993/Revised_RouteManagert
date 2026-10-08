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
        [Draw(DrawType.ToggleGroup, Label = "Reversing strategy")] public ReversingStrategy ReversingStrategy = ReversingStrategy.ChooseBest;
        [Header("Compatibility")]
        [Draw("Respect DV Signals reservations", DrawType.Toggle)] public bool RespectDVSignalsReservations = true;
        [Draw("Reserve next DV Signals block for active route", DrawType.Toggle)] public bool ReserveDVSignalsRoute = true;
        [Header("Keys")]
[Draw(DrawType.KeyBinding)] public KeyBinding TrainEndAlarm = new KeyBinding() { keyCode = KeyCode.N };
        public void OnChange()
        {
            if (!RespectDVSignalsReservations || !ReserveDVSignalsRoute)
                Compatibility.DVSignalsCompatibility.ClearPlayerRouteReservation();
        }

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
