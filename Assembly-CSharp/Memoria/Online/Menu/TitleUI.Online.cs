using Memoria.Online.Menus;
using System;
using UnityEngine;

/// <summary>
/// [Online] The title screen's side of the Online button and lobby (Memoria/Online/Menu/TitleOnline.cs): what it
/// needs from TitleUI's private parts, and the calls TitleUI.cs makes into it (each marked [Online] there).
/// </summary>
public partial class TitleUI
{
    [NonSerialized]
    private TitleOnline onlineTitle;

    internal String OnlineMenuGroup => TitleUI.MenuGroupButton;

    /// <summary>The idle slideshow waits while an online window is open.</summary>
    internal void OnlineIdleTimer(Boolean run)
    {
        if (this.timer == null)
            return;
        if (run)
            this.timer.Start();
        else
            this.timer.Stop();
    }

    private void OnlineAwake()
    {
        this.onlineTitle = new TitleOnline(this, this.MenuLanguageButton);
    }

    private Boolean OnlineConfirm(GameObject go) => this.onlineTitle != null && this.onlineTitle.OnConfirm(go);
    private Boolean OnlineCancel(GameObject go) => this.onlineTitle != null && this.onlineTitle.OnCancel(go);
}
