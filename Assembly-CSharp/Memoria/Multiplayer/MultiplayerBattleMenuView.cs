using Assets.Scripts.Common;
using System;
using UnityEngine;

namespace Memoria.Multiplayer
{
    /// <summary>Draws the compact battle menus of BattleHUD.Multiplayer.cs (Phase 4). Created by BattleHUD on first use.</summary>
    public class MultiplayerBattleMenuView : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void OnGUI()
        {
            if (!MultiplayerManager.IsActive || !SceneDirector.IsBattleScene())
                return;
            try
            {
                UIManager.Battle?.DrawMultiplayerMenus();
            }
            catch (Exception)
            {
                // a menu drawn during a battle transition; the next frame redraws it
            }
        }
    }
}
