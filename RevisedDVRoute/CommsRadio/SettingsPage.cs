using CommandTerminal;
using DV;
using RevisedDVRoute.Internals;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace RevisedDVRoute.CommsRadio
{
    public class SettingsPage : CRMSelectorPage
    {
        public SettingsPage(ICRMPageManager manager) :
            base(manager)
        {
        }

        protected override List<MenuItem> CreateMenuItems()
        {
            return new List<MenuItem>()
            {
                new MenuItem("Under construction", "Menu", () => SetPage(typeof(InitPage)))
            };
        }
    }
}
