local BAR_HEIGHT = 36
local TITLE_HEIGHT = 36
local MIN_WIDTH = 180
local MIN_HEIGHT = 120
local WHITE = { 240, 244, 250 }
local BLACK = { 12, 16, 24 }
local BLUE = { 42, 94, 170 }
local DARK = { 30, 38, 52 }
local LIGHT = { 52, 62, 80 }

local screen_width, screen_height = sys_gui_size()
local desktop_height = math.max(1, screen_height - BAR_HEIGHT)
local windows = {}
local applications = {}
local focused = nil
local menu_open = false
local menu_error = nil
local menu_selection = 1
local menu_scroll = 0
local previous_mouse = { x = 0, y = 0, left = false }
local interaction = nil
local next_window_id = 1
local cascade = 0
local toggle_maximize
local wm_command = ...

local function contains(rect, x, y)
    return x >= rect.x and y >= rect.y and x < rect.x + rect.w and y < rect.y + rect.h
end

local function clamp(value, low, high)
    return math.max(low, math.min(value, high))
end

local function wrap_text(text, columns)
    local lines = {}
    local function append_paragraph(paragraph)
        local current = ""
        for word in paragraph:gmatch("%S+") do
            local remaining = word
            while #remaining > columns do
                if #current > 0 then
                    lines[#lines + 1] = current
                    current = ""
                end
                lines[#lines + 1] = remaining:sub(1, columns)
                remaining = remaining:sub(columns + 1)
            end

            if #remaining > 0 then
                if #current > 0 and #current + 1 + #remaining > columns then
                    lines[#lines + 1] = current
                    current = remaining
                elseif #current > 0 then
                    current = current .. " " .. remaining
                else
                    current = remaining
                end
            end
        end
        lines[#lines + 1] = current
    end

    for paragraph in (tostring(text) .. "\n"):gmatch("(.-)\n") do
        append_paragraph(paragraph)
    end
    return lines
end

local function draw_rect(x, y, width, height, color, filled)
    if width <= 0 or height <= 0 then return end
    sys_gui_rect(x, y, width, height, color[1], color[2], color[3], filled ~= false)
end

local function draw_text(x, y, text, color)
    sys_gui_text(x, y, tostring(text), color[1], color[2], color[3])
end

local function draw_triangle(x, y, color)
    sys_gui_line(x, y, x + 12, y + 8, color[1], color[2], color[3])
    sys_gui_line(x + 12, y + 8, x, y + 16, color[1], color[2], color[3])
    sys_gui_line(x, y + 16, x, y, color[1], color[2], color[3])
end

local function client_rect(win)
    return {
        x = win.x + 2,
        y = win.y + TITLE_HEIGHT + 2,
        w = math.max(1, win.w - 4),
        h = math.max(1, win.h - TITLE_HEIGHT - 4),
    }
end

local function make_context(win)
    local client = client_rect(win)
    local context = { width = client.w, height = client.h }

    function context:clear(color)
        draw_rect(client.x, client.y, client.w, client.h, color or { 18, 22, 30 })
    end

    function context:rect(x, y, width, height, color, filled)
        local left = clamp(math.floor(x), 0, client.w)
        local top = clamp(math.floor(y), 0, client.h)
        local right = clamp(math.floor(x + width), 0, client.w)
        local bottom = clamp(math.floor(y + height), 0, client.h)
        if right > left and bottom > top then
            draw_rect(client.x + left, client.y + top, right - left, bottom - top, color, filled)
        end
    end

    function context:pixel(x, y, color)
        if x >= 0 and y >= 0 and x < client.w and y < client.h then
            sys_gui_pixel(client.x + math.floor(x), client.y + math.floor(y), color[1], color[2], color[3])
        end
    end

    function context:line(x1, y1, x2, y2, color)
        if x1 >= 0 and y1 >= 0 and x2 >= 0 and y2 >= 0
            and x1 < client.w and x2 < client.w and y1 < client.h and y2 < client.h then
            sys_gui_line(client.x + math.floor(x1), client.y + math.floor(y1),
                client.x + math.floor(x2), client.y + math.floor(y2), color[1], color[2], color[3])
        end
    end

    function context:text(x, y, text, color)
        x = math.floor(x)
        y = math.floor(y)
        if y < 0 or y >= client.h or x < 0 or x >= client.w then return end

        local columns = math.floor((client.w - x) / 16)
        local rows = math.floor((client.h - y) / 32)
        if columns < 1 or rows < 1 then return end

        local lines = wrap_text(text, columns)
        for index = 1, math.min(#lines, rows) do
            if #lines[index] > 0 then
                draw_text(client.x + x, client.y + y + (index - 1) * 32, lines[index], color or WHITE)
            end
        end
    end

    return context
end

local function focus(win)
    if win and win.minimized then win.minimized = false end
    focused = win
    if win then
        for index, item in ipairs(windows) do
            if item == win then
                table.remove(windows, index)
                windows[#windows + 1] = win
                break
            end
        end
    end
end

local function create_window(options)
    options = options or {}
    cascade = (cascade + 28) % 168
    local width = clamp(math.floor(options.width or 480), math.min(MIN_WIDTH, screen_width), screen_width)
    local height = clamp(math.floor(options.height or 320), math.min(MIN_HEIGHT, desktop_height), desktop_height)
    local win = {
        id = next_window_id,
        title = tostring(options.title or "Window"),
        program = tostring(options.program or options.title or "Application"),
        x = math.floor(options.x or (48 + cascade)),
        y = math.floor(options.y or (BAR_HEIGHT + 36 + cascade)),
        w = width,
        h = height,
        draw = options.draw,
        on_key = options.on_key,
        on_mouse = options.on_mouse,
        minimized = false,
        maximized = false,
        restore = nil,
        error = nil,
    }
    next_window_id = next_window_id + 1
    win.x = clamp(win.x, 0, math.max(0, screen_width - win.w))
    win.y = clamp(win.y, BAR_HEIGHT, math.max(BAR_HEIGHT, screen_height - win.h))
    windows[#windows + 1] = win
    focus(win)
    function win:minimize()
        self.minimized = true
        if focused == self then
            focused = nil
            for index = #windows, 1, -1 do
                if not windows[index].minimized then focused = windows[index]; break end
            end
        end
    end
    function win:maximize()
        toggle_maximize(self)
    end
    function win:close()
        for index, item in ipairs(windows) do
            if item == self then table.remove(windows, index); break end
        end
        if focused == self then
            focused = nil
            for index = #windows, 1, -1 do
                if not windows[index].minimized then focused = windows[index]; break end
            end
        end
    end
    return win
end

local gui = {
    screen_width = screen_width,
    screen_height = screen_height,
    bar_height = BAR_HEIGHT,
    colors = { white = WHITE, black = BLACK, blue = BLUE, dark = DARK, light = LIGHT },
    create_window = create_window,
    shell_user = wm_command and wm_command.username or "user",
    shell_path = wm_command and wm_command.path or { [1] = "/prog/sys", [2] = "/prog/usr" },
}

function gui.resolve_path(cwd, path)
    if path:sub(1, 1) == "/" then cwd = "" end
    local parts = {}
    for part in (cwd .. "/" .. path):gmatch("[^/]+") do
        if part == ".." then
            if #parts > 0 then table.remove(parts) end
        elseif part ~= "." then
            parts[#parts + 1] = part
        end
    end
    return "/" .. table.concat(parts, "/")
end

local function collect_apps(path)
    local entries = sys_listdir(path)
    if not entries then return end
    for _, entry in ipairs(entries) do
        local child = path .. "/" .. entry.name
        if entry.kind == "dir" then
            collect_apps(child)
        elseif entry.name:sub(-4):lower() == ".lua" then
            applications[#applications + 1] = { path = child, name = entry.name }
        end
    end
end

local function launch_app(app)
    menu_open = false
    menu_error = nil
    local chunk, err = loadfile(app.path)
    if not chunk then
        menu_error = tostring(err)
        return
    end
    local ok, result = pcall(chunk, gui)
    if not ok then
        menu_error = tostring(result)
    elseif type(result) == "function" then
        local app_ok, app_error = pcall(result, gui)
        if not app_ok then menu_error = tostring(app_error) end
    end
end

local function window_controls(win)
    local right = win.x + win.w - 4
    return {
        close = { x = right - 25, y = win.y + 7, w = 20, h = 22 },
        maximize = { x = right - 49, y = win.y + 7, w = 20, h = 22 },
        minimize = { x = right - 73, y = win.y + 7, w = 20, h = 22 },
    }
end

toggle_maximize = function(win)
    if win.maximized then
        win.x, win.y, win.w, win.h = win.restore.x, win.restore.y, win.restore.w, win.restore.h
        win.restore = nil
        win.maximized = false
    else
        win.restore = { x = win.x, y = win.y, w = win.w, h = win.h }
        win.x, win.y, win.w, win.h = 0, BAR_HEIGHT, screen_width, desktop_height
        win.maximized = true
    end
end

local function draw_window(win)
    draw_rect(win.x, win.y, win.w, win.h, { 8, 10, 16 })
    draw_rect(win.x + 2, win.y + 2, win.w - 4, win.h - 4, DARK)
    draw_rect(win.x + 2, win.y + 2, win.w - 4, TITLE_HEIGHT, win == focused and BLUE or LIGHT)
    local controls = window_controls(win)
    local title_columns = math.max(1, math.floor((controls.minimize.x - win.x - 16) / 16))
    draw_text(win.x + 10, win.y + 3, win.title:sub(1, title_columns), WHITE)
    draw_rect(controls.minimize.x, controls.minimize.y, controls.minimize.w, controls.minimize.h, { 80, 90, 110 })
    draw_rect(controls.maximize.x, controls.maximize.y, controls.maximize.w, controls.maximize.h, { 80, 90, 110 })
    draw_rect(controls.close.x, controls.close.y, controls.close.w, controls.close.h, { 150, 58, 65 })
    draw_text(controls.minimize.x + 2, controls.minimize.y - 5, "_", WHITE)
    draw_text(controls.maximize.x + 2, controls.maximize.y - 5, win.maximized and "o" or "+", WHITE)
    draw_text(controls.close.x + 2, controls.close.y - 5, "x", WHITE)

    if win.error then
        local context = make_context(win)
        local client = client_rect(win)
        context:text(8, 8, "Application error:", { 255, 120, 120 })
        context:text(8, 40, win.error, WHITE)
        draw_rect(client.x + client.w - 30, client.y + 6, 24, 24, { 150, 58, 65 })
        draw_text(client.x + client.w - 26, client.y + 1, "x", WHITE)
    elseif win.draw then
        local ok, err = pcall(win.draw, make_context(win), win)
        if not ok then win.error = tostring(err) end
    end

    if not win.maximized then
        draw_rect(win.x + win.w - 14, win.y + win.h - 14, 12, 12, { 140, 155, 175 })
    end
end

local function menu_bounds()
    local max_name = 0
    for _, app in ipairs(applications) do max_name = math.max(max_name, #app.name) end
    return {
        x = 0,
        y = BAR_HEIGHT,
        w = clamp((max_name + 3) * 16, 176, 360),
        h = math.max(80, math.min(#applications * 36 + 44, screen_height - BAR_HEIGHT)),
    }
end

local function menu_error_layout()
    local lines = wrap_text(menu_error or "", math.max(1, math.floor((screen_width - 56) / 16)))
    local visible_rows = math.max(1, math.floor((screen_height - BAR_HEIGHT) / 32))
    local first_line = math.max(1, #lines - visible_rows + 1)
    local displayed_rows = #lines - first_line + 1
    local height = displayed_rows * 32 + 8
    return {
        lines = lines,
        first_line = first_line,
        x = 0,
        y = screen_height - height,
        w = screen_width,
        h = height,
        dismiss = { x = screen_width - 32, y = screen_height - height + 4, w = 24, h = 24 },
    }
end

local function app_hit(x, y)
    local menu = menu_bounds()
    if not contains(menu, x, y) then return nil end
    local index = math.floor((y - menu.y - 40) / 36) + 1 + menu_scroll
    return applications[index]
end

local function hit_window(x, y)
    for index = #windows, 1, -1 do
        local win = windows[index]
        if not win.minimized and contains({ x = win.x, y = win.y, w = win.w, h = win.h }, x, y) then
            return win
        end
    end
end

local function hit_menu_button(x, y)
    return y < BAR_HEIGHT and x < 112
end

local function handle_click(x, y)
    if menu_error then
        local layout = menu_error_layout()
        if contains(layout.dismiss, x, y) then
            menu_error = nil
            return
        end
    end

    if y < BAR_HEIGHT then
        if hit_menu_button(x, y) then
            menu_open = not menu_open
            if menu_open then
                applications = {}
                collect_apps("/prog/gui")
                table.sort(applications, function(a, b) return a.name:lower() < b.name:lower() end)
                menu_selection = 1
                menu_scroll = 0
            end
            return
        end

        local tab_x = 240
        for _, win in ipairs(windows) do
            local tab_w = math.min(200, math.max(112, (#win.title + 2) * 16))
            if x >= tab_x and x < tab_x + tab_w then
                focus(win)
                return
            end
            tab_x = tab_x + tab_w + 4
        end
    end

    if menu_open then
        local app = app_hit(x, y)
        if app then launch_app(app) else menu_open = false end
        return
    end

    local win = hit_window(x, y)
    if not win then
        focused = nil
        return
    end
    focus(win)

    local controls = window_controls(win)
    if contains(controls.close, x, y) then
        for index, item in ipairs(windows) do
            if item == win then table.remove(windows, index); break end
        end
        if focused == win then focused = windows[#windows] end
        return
    elseif contains(controls.minimize, x, y) then
        win.minimized = true
        focused = nil
        for index = #windows, 1, -1 do
            if not windows[index].minimized then focused = windows[index]; break end
        end
        return
    elseif contains(controls.maximize, x, y) then
        toggle_maximize(win)
        return
    end

    if not win.maximized and y < win.y + TITLE_HEIGHT and x < controls.minimize.x then
        interaction = { kind = "move", win = win, dx = x - win.x, dy = y - win.y }
        return
    end

    local grip = { x = win.x + win.w - 18, y = win.y + win.h - 18, w = 18, h = 18 }
    if not win.maximized and contains(grip, x, y) then
        interaction = { kind = "resize", win = win, x = x, y = y, w = win.w, h = win.h }
        return
    end

    local client = client_rect(win)
    local error_dismiss = { x = client.x + client.w - 30, y = client.y + 6, w = 24, h = 24 }
    if win.error and contains(error_dismiss, x, y) then
        win.error = nil
        return
    end

    if win.on_mouse and contains(client, x, y) then
        local ok, err = pcall(win.on_mouse, x - client.x, y - client.y, true, win)
        if not ok then win.error = tostring(err) end
    end
end

local function handle_mouse()
    local x, y, left = sys_gui_mouse()
    local pressed = left and not previous_mouse.left
    local released = not left and previous_mouse.left

    if pressed then handle_click(x, y) end
    if interaction and left then
        local win = interaction.win
        if interaction.kind == "move" then
            win.x = clamp(x - interaction.dx, 0, math.max(0, screen_width - win.w))
            win.y = clamp(y - interaction.dy, BAR_HEIGHT, math.max(BAR_HEIGHT, screen_height - win.h))
        elseif interaction.kind == "resize" then
            win.w = clamp(interaction.w + x - interaction.x, MIN_WIDTH, math.max(MIN_WIDTH, screen_width - win.x))
            win.h = clamp(interaction.h + y - interaction.y, MIN_HEIGHT, math.max(MIN_HEIGHT, screen_height - win.y))
        end
    end
    if released then interaction = nil end

    previous_mouse.x, previous_mouse.y, previous_mouse.left = x, y, left
end

local function handle_key()
    local key, character, control = sys_gui_key()
    if not key then return end
    if menu_open then
        if key == "UpArrow" then
            menu_selection = math.max(1, menu_selection - 1)
            if menu_selection <= menu_scroll then menu_scroll = menu_selection - 1 end
        elseif key == "DownArrow" then
            menu_selection = math.min(#applications, menu_selection + 1)
            local visible = math.max(1, math.floor((screen_height - BAR_HEIGHT - 40) / 36))
            if menu_selection > menu_scroll + visible then menu_scroll = menu_selection - visible end
        elseif key == "Enter" and applications[menu_selection] then
            launch_app(applications[menu_selection])
        elseif key == "Escape" then
            menu_open = false
        end
        return
    end
    if focused and focused.on_key then
        local ok, err = pcall(focused.on_key, key, character, control, focused)
        if not ok then focused.error = tostring(err) end
    end
end

local function draw_top_bar()
    draw_rect(0, 0, screen_width, BAR_HEIGHT, { 18, 23, 33 })
    draw_rect(0, BAR_HEIGHT - 2, screen_width, 2, BLUE)
    draw_rect(0, 0, 112, BAR_HEIGHT, menu_open and BLUE or { 36, 45, 60 })
    draw_text(12, 2, "progs", WHITE)

    local x = 240
    for _, win in ipairs(windows) do
        local width = math.min(200, math.max(112, (#win.title + 2) * 16))
        draw_rect(x, 3, width, BAR_HEIGHT - 6, win == focused and BLUE or LIGHT)
        local label = win.minimized and "[" .. win.title .. "]" or win.title
        draw_text(x + 8, 2, label:sub(1, math.floor((width - 16) / 16)), WHITE)
        x = x + width + 4
    end
end

local function draw_menu()
    if not menu_open then return end
    local menu = menu_bounds()
    draw_rect(menu.x, menu.y, menu.w, menu.h, { 14, 18, 27 })
    draw_rect(menu.x, menu.y, menu.w, 34, BLUE)
    draw_text(menu.x + 10, menu.y + 1, "Programs", WHITE)
    local visible = math.max(0, math.floor((menu.h - 40) / 36))
    local last = math.min(#applications, menu_scroll + visible)
    local y = menu.y + 40
    for index = menu_scroll + 1, last do
        if index == menu_selection then draw_rect(menu.x + 2, y - 2, menu.w - 4, 34, BLUE) end
        draw_text(menu.x + 10, y, applications[index].name:sub(1, math.floor((menu.w - 20) / 16)), WHITE)
        y = y + 36
    end
    if #applications == 0 then draw_text(menu.x + 10, menu.y + 42, "No Lua apps found", WHITE) end
end

previous_mouse.x, previous_mouse.y, previous_mouse.left = sys_gui_mouse()
sys_gui_clear(24, 29, 40)
draw_text(32, 48, "XSNOS window manager", WHITE)
draw_text(32, 88, "Choose Apps to launch a GUI program.", WHITE)
sys_gui_present()

while true do
    handle_mouse()
    handle_key()

    sys_gui_clear(24, 29, 40)
    draw_rect(0, BAR_HEIGHT, screen_width, desktop_height, { 24, 29, 40 })
    for _, win in ipairs(windows) do
        if not win.minimized then draw_window(win) end
    end
    draw_top_bar()
    draw_menu()
    if menu_error then
        local layout = menu_error_layout()
        draw_rect(layout.x, layout.y, layout.w, layout.h, { 115, 38, 46 })
        draw_rect(layout.dismiss.x, layout.dismiss.y, layout.dismiss.w, layout.dismiss.h, { 150, 58, 65 })
        draw_text(layout.dismiss.x + 4, layout.dismiss.y - 5, "x", WHITE)
        for line_index = layout.first_line, #layout.lines do
            draw_text(8, layout.y + 4 + (line_index - layout.first_line) * 32, layout.lines[line_index], WHITE)
        end
    end
    draw_triangle(previous_mouse.x, previous_mouse.y, WHITE)
    sys_gui_present()
    sys_gui_wait(16)
end
