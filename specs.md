# Description
Small utility that interacts with claude code and notifies the user when it is waiting for user's input.

# Task
1. Go through this spec sheet and figure out what needs to be done
2. Highlight some issues with developing this app and iron out the interface details.
3. Finalize on a UI by talking to me. Present with some choices
4. Suggest a good name for the app. Move on to next step after I have approved a name.
5. Go ahead and develop the app.

# Interface
## Main Interface
1. App stays in the tray icons in the task bar
2. Shows a starred mark when there is a claude notification
3. Right click menu
  - advanced settings
  - Display tabs

### Advanced settings dialog
1. Display tabs: This will be a checkbox. The display tabs feature will be enabled based on this checkbox.
2. Always on top: (disabled if display tabs checkbox is unchecked) Tabs will stay always on top
3. Claude hooks: Enable/disable all the hooks available. All hooks are enabled by default.
4. Tabs position: Lets user select out of - upper left, lower left, upper right, lower right.
5. Tabs display screen: (Disabled if the user only has single screen)
6. Notification sound: Checkbox and let user select out of selection of sounds. Keep Chimes as default sound from window's collection.
7. Setup claude settings: Come up with a better name/description for this option. Clicking on this button will add the settings to user level .json file to send trigger to this app. Disabled if setup is already done

### Display tabs
- These are the tabs with each tab being associated with different claude code session running.
- Tabs are stacked vertically.
- Each tab should have name of the session displayed.
- Tabs width should be resizable (resizing changes the width of all tabs)
- Tabs should be collapsible. When expanded, it shows the question/permission that claude is asking for that session.
- Provide a button to open the respective session on each tab. Clicking the button would open the Terminal or VScode session where claude code is requesting this permission.