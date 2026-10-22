local function fit(text, width)
    text = string.sub(text, 1, width)
    return text .. string.rep(" ", math.max(0, width - #text))
end

local function main()
    io.write("txted2 path: ")
    io.flush()
    local path = io.read("*l")
    if path == nil or path == "" then
        return
    end

    local lines = {}
    local input, open_error = io.open(path, "r")
    if input then
        for line in input:lines() do
            lines[#lines + 1] = line:gsub("\r$", "")
        end
        input:close()
    elseif not tostring(open_error):find("No such file", 1, true) then
        print("txted2: " .. tostring(open_error))
        return
    end

    if #lines == 0 then
        lines[1] = ""
    end

    local screen_height = math.max(sys_scrh(), 7)
    local screen_width = math.max(sys_scrw(), 20)
    local screen_rows = math.max(screen_height - 2, 5)
    local screen_columns = screen_width - 1
    local row, column, top_row, left_column = 1, 0, 1, 0
    local dirty = false
    local running = true

    local function cursor_x()
        return math.max(0, math.min(column - left_column, screen_columns - 1))
    end

    local function cursor_y()
        return math.max(0, math.min(row - top_row + 1, screen_height - 1))
    end

    local function write_at(x, y, text)
        sys_putat(x, y, text, cursor_x(), cursor_y())
    end

    local function update_header()
        write_at(0, 0, fit("txted2  " .. path .. (dirty and " *" or "") .. "   ^S save   ^Q quit", screen_columns))
    end

    local function update_line(line_index, from_column)
        local screen_y = line_index - top_row + 1
        if screen_y < 1 or screen_y > screen_rows then
            return
        end

        local screen_x = math.max(0, from_column - left_column)
        if screen_x >= screen_columns then
            return
        end

        local first_column = left_column + screen_x + 1
        local text = string.sub(lines[line_index] or "", first_column, left_column + screen_columns)
        write_at(screen_x, screen_y, fit(text, screen_columns - screen_x))
    end

    local function update_visible_rows(from_line)
        for line_index = math.max(from_line, top_row), top_row + screen_rows - 1 do
            update_line(line_index, left_column)
        end
    end

    local function redraw_all()
        sys_clear()
        update_header()
        update_visible_rows(top_row)
        sys_goto(cursor_x(), cursor_y())
        io.flush()
    end

    local function keep_cursor_visible()
        local old_top = top_row
        local old_left = left_column

        if row < top_row then
            top_row = row
        elseif row >= top_row + screen_rows then
            top_row = row - screen_rows + 1
        end
        if column < left_column then
            left_column = column
        elseif column >= left_column + screen_columns then
            left_column = column - screen_columns + 1
        end

        return old_top ~= top_row or old_left ~= left_column
    end

    local function save()
        local output, err = io.open(path, "w")
        if not output then
            sys_goto(0, screen_rows + 1)
            io.write("save failed: " .. tostring(err))
            io.flush()
            sys_readkey()
            return
        end

        for _, line in ipairs(lines) do
            output:write(line, "\n")
        end

        local ok, close_error = output:close()
        if not ok then
            sys_goto(0, screen_rows + 1)
            io.write("save failed: " .. tostring(close_error))
            io.flush()
            sys_readkey()
            return
        end

        dirty = false
    end

    sys_cursor(true)
    redraw_all()

    while running do
        local key, character, control = sys_readkey()
        local old_row, old_column = row, column
        local changed_from = nil
        local structure_changed = false

        if control and key == "S" then
            save()
            update_header()
        elseif control and key == "Q" then
            running = false
        elseif key == "UpArrow" then
            if row > 1 then
                row = row - 1
                column = math.min(column, #lines[row])
            end
        elseif key == "DownArrow" then
            if row < #lines then
                row = row + 1
                column = math.min(column, #lines[row])
            end
        elseif key == "LeftArrow" then
            if column > 0 then
                column = column - 1
            elseif row > 1 then
                row = row - 1
                column = #lines[row]
            end
        elseif key == "RightArrow" then
            if column < #lines[row] then
                column = column + 1
            elseif row < #lines then
                row = row + 1
                column = 0
            end
        elseif key == "Home" then
            column = 0
        elseif key == "End" then
            column = #lines[row]
        elseif key == "Enter" then
            local split_at = column
            local current_line = lines[row]
            lines[row] = string.sub(current_line, 1, split_at)
            table.insert(lines, row + 1, string.sub(current_line, split_at + 1))
            changed_from = column
            row = row + 1
            column = 0
            dirty = true
            structure_changed = true
        elseif key == "Backspace" then
            if column > 0 then
                lines[row] = string.sub(lines[row], 1, column - 1) .. string.sub(lines[row], column + 1)
                changed_from = column - 1
                column = column - 1
                dirty = true
            elseif row > 1 then
                column = #lines[row - 1]
                lines[row - 1] = lines[row - 1] .. lines[row]
                table.remove(lines, row)
                row = row - 1
                dirty = true
                changed_from = column
                structure_changed = true
            end
        elseif key == "Delete" then
            if column < #lines[row] then
                lines[row] = string.sub(lines[row], 1, column) .. string.sub(lines[row], column + 2)
                changed_from = column
                dirty = true
            elseif row < #lines then
                lines[row] = lines[row] .. lines[row + 1]
                table.remove(lines, row + 1)
                dirty = true
                changed_from = column
                structure_changed = true
            end
        elseif not control and character ~= "" then
            local insert_at = column
            lines[row] = string.sub(lines[row], 1, insert_at) .. character .. string.sub(lines[row], insert_at + 1)
            changed_from = column
            column = column + #character
            dirty = true
        end

        if running then
            local view_changed = keep_cursor_visible()
            if view_changed then
                redraw_all()
            else
                if structure_changed then
                    update_visible_rows(math.min(old_row, row))
                elseif changed_from ~= nil then
                    update_line(row, changed_from)
                end
                if dirty and old_row == row and old_column == column and changed_from == nil then
                    sys_goto(cursor_x(), cursor_y())
                elseif changed_from == nil then
                    sys_goto(cursor_x(), cursor_y())
                end
                if dirty and changed_from ~= nil then
                    update_header()
                end
                io.flush()
            end
        end
    end

    sys_clear()
end

local ok, err = pcall(main)
sys_cursor(true)
sys_clear()
if not ok then
    print("textedit2: " .. tostring(err))
end