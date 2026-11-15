return function(gui)
    local history = {}
    local partial = ""
    local input = ""
    local input_prompt = nil
    local scroll = 0
    local active_context = nil
    local window = nil
    local shell
    local console_mode = false
    local console_rows = {}
    local console_cursor = { x = 0, y = 0, visible = true }
    local console_color = gui.colors.white

    local function append(text)
        text = tostring(text)
        for index = 1, #text do
            local character = text:sub(index, index)
            if character == "\n" then
                history[#history + 1] = partial
                partial = ""
                if #history > 200 then table.remove(history, 1) end
            else
                partial = partial .. character
            end
        end
        scroll = 0
    end

    local function wrap_input(text, columns)
        local lines = {}
        local current = ""

        for word in tostring(text):gmatch("%S+") do
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

        if #current > 0 or #lines == 0 then
            lines[#lines + 1] = current
        end
        return lines
    end

    local function wrap_history_line(text, columns)
        local lines = {}
        local remaining = tostring(text)

        while #remaining > columns do
            local break_at = nil
            for index = 1, columns do
                if remaining:sub(index, index):match("%s") then
                    break_at = index
                end
            end

            if break_at then
                lines[#lines + 1] = remaining:sub(1, break_at)
                remaining = remaining:sub(break_at + 1)
            else
                lines[#lines + 1] = remaining:sub(1, columns)
                remaining = remaining:sub(columns + 1)
            end
        end

        lines[#lines + 1] = remaining
        return lines
    end

    local function draw(context)
        active_context = context
        context:clear({ 12, 16, 22 })
        if console_mode then
            for row, text in ipairs(console_rows) do
                context:text(0, (row - 1) * 32, text, console_color)
            end
            if console_cursor.visible then
                context:text(console_cursor.x * 16, console_cursor.y * 32, "_", console_color)
            end
            return
        end

        local prompt = input_prompt or (shell and shell:prompt() or "")
        local input_lines = wrap_input(prompt .. input .. (input_prompt and "_" or ""), math.max(1, math.floor((context.width - 8) / 16)))
        local total_rows = math.max(1, math.floor(context.height / 32))
        local input_rows = math.min(#input_lines, total_rows)
        local history_rows = total_rows - input_rows
        local columns = math.max(1, math.floor((context.width - 8) / 16))
        local history_lines = {}
        for _, line in ipairs(history) do
            for _, wrapped_line in ipairs(wrap_history_line(line, columns)) do
                history_lines[#history_lines + 1] = wrapped_line
            end
        end
        if partial ~= "" then
            for _, wrapped_line in ipairs(wrap_history_line(partial, columns)) do
                history_lines[#history_lines + 1] = wrapped_line
            end
        end

        scroll = math.min(scroll, math.max(0, #history_lines - history_rows))
        local last = #history_lines - scroll
        local first = math.max(1, last - history_rows + 1)
        local y = 0

        for index = first, last do
            if history_rows > 0 and y < history_rows * 32 then
                context:text(8, y, history_lines[index], gui.colors.white)
                y = y + 32
            end
        end

        local first_input_line = #input_lines - input_rows + 1
        local input_y = context.height - input_rows * 32
        for index = first_input_line, #input_lines do
            context:text(8, input_y, input_lines[index], { 130, 210, 255 })
            input_y = input_y + 32
        end
    end

    local function refresh_blocking_read()
        if active_context then
            draw(active_context)
            sys_gui_present()
        end
        sys_gui_wait(16)
    end

    local function read_line()
        input = ""
        input_prompt = partial
        partial = ""

        while true do
            local key, character, control = sys_gui_key()
            if key == "Enter" then
                local result = input
                history[#history + 1] = (input_prompt or "") .. result
                if #history > 200 then table.remove(history, 1) end
                input = ""
                input_prompt = nil
                scroll = 0
                return result
            elseif key == "Backspace" then
                input = input:sub(1, -2)
            elseif key == "UpArrow" then
                scroll = scroll + 1
            elseif key == "DownArrow" then
                scroll = math.max(0, scroll - 1)
            elseif character and character ~= "" and not control then
                input = input .. character
            elseif key == "Escape" then
                input_prompt = nil
                return nil
            end
            refresh_blocking_read()
        end
    end

    local function read_key()
        while true do
            local key, character, control = sys_gui_key()
            if key then return key, character, control end
            refresh_blocking_read()
        end
    end

    local function draw_console_text(x, y, text)
        if not active_context then return end
        local columns = math.max(1, math.floor(active_context.width / 16))
        local rows = math.max(1, math.floor(active_context.height / 32))
        x = math.max(0, math.min(math.floor(x), columns - 1))
        y = math.max(0, math.min(math.floor(y), rows - 1))
        text = tostring(text):sub(1, columns - x)
        local row = console_rows[y + 1] or ""
        if #row < x then row = row .. string.rep(" ", x - #row) end
        local prefix = row:sub(1, x)
        local suffix = row:sub(x + #text + 1)
        console_rows[y + 1] = (prefix .. text .. suffix):sub(1, columns)
        draw(active_context)
    end

    local function capture_output(callback)
        local old_print = print
        local old_write = io.write
        local old_read = io.read
        local old_flush = io.flush
        local old_stdin = io.stdin
        local old_stdout = io.stdout
        local old_scrh = sys_scrh
        local old_scrw = sys_scrw
        local old_green = sys_green
        local old_white = sys_white
        local old_readkey = sys_readkey
        local old_goto = sys_goto
        local old_clear = sys_clear
        local old_cursor = sys_cursor
        local old_putat = sys_putat

        print = function(...)
            local values = {}
            for index = 1, select("#", ...) do
                values[index] = tostring(select(index, ...))
            end
            append(table.concat(values, "\t") .. "\n")
        end
        io.write = function(...)
            for index = 1, select("#", ...) do
                append(select(index, ...))
            end
            return true
        end
        io.read = function()
            return read_line()
        end
        io.flush = function()
            return true
        end
        io.stdin = {
            read = function()
                return read_line()
            end,
        }
        io.stdout = {
            write = function(_, ...)
                for index = 1, select("#", ...) do
                    append(select(index, ...))
                end
                return true
            end,
            flush = function()
                return true
            end,
        }
        sys_scrh = function()
            return active_context and math.max(1, math.floor(active_context.height / 32)) or 1
        end
        sys_scrw = function()
            return active_context and math.max(1, math.floor(active_context.width / 16)) or 1
        end
        sys_green = function()
            console_color = { 0, 255, 0 }
        end
        sys_white = function()
            console_color = gui.colors.white
        end
        sys_readkey = read_key
        sys_goto = function(x, y)
            console_cursor.x = math.max(0, math.floor(x))
            console_cursor.y = math.max(0, math.floor(y))
            if console_mode and active_context then draw(active_context) end
        end
        sys_clear = function()
            console_mode = true
            console_rows = {}
            if active_context then
                local rows = math.max(1, math.floor(active_context.height / 32))
                for index = 1, rows do console_rows[index] = "" end
                draw(active_context)
            end
        end
        sys_cursor = function(visible)
            console_cursor.visible = visible
            if console_mode and active_context then draw(active_context) end
        end
        sys_putat = function(x, y, text, cursor_x, cursor_y)
            console_mode = true
            draw_console_text(x, y, text)
            console_cursor.x = math.max(0, math.floor(cursor_x))
            console_cursor.y = math.max(0, math.floor(cursor_y))
            if active_context then draw(active_context) end
        end

        local ok, result = pcall(callback)
        print = old_print
        io.write = old_write
        io.read = old_read
        io.flush = old_flush
        io.stdin = old_stdin
        io.stdout = old_stdout
        sys_scrh = old_scrh
        sys_scrw = old_scrw
        sys_green = old_green
        sys_white = old_white
        sys_readkey = old_readkey
        sys_goto = old_goto
        sys_clear = old_clear
        sys_cursor = old_cursor
        sys_putat = old_putat
        console_mode = false
        if not ok then
            append("E: " .. tostring(result) .. "\n")
        end
        return ok, result
    end

    local shell_chunk, load_error = loadfile("/sys/xsnsh.lua")
    if not shell_chunk then
        error("could not load xsnsh.lua: " .. tostring(load_error))
    end

    local _, gui_shell = capture_output(function()
        return shell_chunk(gui.shell_user, gui.shell_path, { gui = true })
    end)
    shell = gui_shell
    if type(shell) ~= "table" or type(shell.run_line) ~= "function" then
        error("xsnsh.lua did not return its GUI shell interface")
    end

    local function on_key(key, character, control)
        if key == "Enter" then
            local command = input
            append(shell:prompt() .. command .. "\n")
            input = ""
            local _, exiting = capture_output(function()
                return shell.run_line(command)
            end)
            if exiting then
                window:close()
            end
        elseif key == "Backspace" then
            input = input:sub(1, -2)
        elseif key == "UpArrow" then
            scroll = scroll + 1
        elseif key == "DownArrow" then
            scroll = math.max(0, scroll - 1)
        elseif character and character ~= "" and not control then
            input = input .. character
        end
    end

    window = gui.create_window({
        title = "XSNOS Shell",
        program = "XSNOS Shell",
        width = math.min(720, gui.screen_width - 24),
        height = math.min(440, gui.screen_height - gui.bar_height - 24),
        draw = draw,
        on_key = on_key,
    })
end
