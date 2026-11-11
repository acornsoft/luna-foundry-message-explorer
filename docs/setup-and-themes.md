# Setup and themes

## Light, dark or system

The app follows the operating system's light or dark setting by default. Pick **System**, **Light** or **Dark**
on the Accounts page, at the top of the Connect panel, or under **Settings**. All three pickers share one
choice, and it is remembered on this computer in `ui-settings.json` in the app's config folder (with the
Queues and Topics toggles and the recent connections list; no secrets).

Colours come from one palette in `App.axaml` with a Light and a Dark dictionary, used through
`DynamicResource`, so views never hard-code a colour. Maroon (#4F2001) is the accent in light and gold
(#FF9900) in dark. Every text colour pair passes 4.5:1 contrast and every icon pair 3:1 in both themes;
`ThemeAndSetupTests` checks the palette, checks that every resource key the views use exists in both
themes, and fails if a view hard-codes a colour.

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
group. **Open** (or double-click) opens a Service Bus namespace in Queues & topics. Event Hubs namespaces are
listed for completeness; **Save as Event Hubs login** saves one to Saved logins (no secret). All of this is
read-only Azure Resource Manager listing.

## Connect panel

**Recent** lists the last ten connections (saved logins by id, and namespaces opened from Browse by their
resource id and account). A pasted connection string that you don't save is never kept. **Saved** lists
saved logins. Below them, **Connection details** still takes a namespace with an Entra account, or a pasted
connection string.

Once connected, the Connect panel steps aside on the Queues & topics and tool tabs so they get the full
width; it stays on Accounts and Saved logins. The plug button in the header (which shows the current
connection) pins it open on every tab.
