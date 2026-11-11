# Setup and themes

## Light, dark or system

The app follows the operating system's light or dark setting by default. Pick **System**, **Light** or **Dark**
under **Settings** (the gear in the top bar), the only place for appearance. The choice is remembered on this computer in `ui-settings.json` in the app's config
folder (with the kind filter and the recent connections list; no secrets).

Colours come from one palette in `App.axaml` with a Light and a Dark dictionary, used through
`DynamicResource`, so views never hard-code a colour. Maroon (#4F2001) is the accent in light and gold
(#FF9900) in dark. Every text colour pair passes 4.5:1 contrast and every icon pair 3:1 in both themes;
`ThemeAndSetupTests` checks the palette, checks that every resource key the views use exists in both
themes, and fails if a view hard-codes a colour. `HeadlessUxPassTests` also measures small text on the
rendered window (kind badges, count badges, "top N ready to peek", mono paths and helper text) in both themes:
each must be at least 10 px and 4.5:1 against what is actually behind it.

## The namespace switcher

The namespace name at the top left opens one sheet with a left rail, in the style of Azure Data Studio's
connection dialog: **Connect** (Recent and Saved on the left, connection details on the right), **Accounts**
and **Saved logins**. It is open at launch on the page with the next action (see [Resources](quick-view.md#the-top-bar)),
closes when a namespace connects, and Esc or a click outside closes it once something is connected. Once
connected, the namespace name opens a quick switch (Recent, Saved, **Manage accounts…**) instead. These used to be the Accounts and Saved logins tabs and the Connect panel.

## Accounts (linked accounts)

Like Azure Data Studio's Linked accounts:

- **Add an account** signs in with the browser (pick which one) or **Sign in with a code** (device code: the
  code is shown large with **Copy code**, and the page to enter it at). **Copy sign-in link** helps when the
  browser doesn't open.
- Each account shows its cloud and state, and expands to its **tenants** with each tenant's subscription
  count or its error. **Refresh** reads one account again; **Refresh** above the list reads them all.
  **Sign out** and **Remove** are per account. Each account keeps its own cloud.

## Browse

Beside the accounts, **Browse** lists subscription, then resource group, then Service Bus and Event Hubs
namespaces, from every signed-in account. Pick one subscription or all, and filter by namespace or resource
group. **Open** (or double-click) opens a Service Bus namespace in Resources. Event Hubs namespaces are
listed for completeness; **Save as Event Hubs login** saves one to Saved logins (no secret). All of this is
read-only Azure Resource Manager listing.

## Connect page

**Recent** lists the last ten connections (saved logins by id, and namespaces opened from Browse by their
resource id and account). A pasted connection string that you don't save is never kept. **Saved** lists
saved logins. Beside them, **Connection details** still takes a namespace with an Entra account, or a pasted
connection string. A disabled **Connect** or **Connect saved** says why on a line under it (for example
"Enter a namespace to connect.").

Once connected, the switcher closes so Resources gets the whole window. Open it again from the namespace name.
