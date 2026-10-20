local function read_integer(prompt)
    io.write(prompt)
    io.flush()
    local value = tonumber(io.read("*l"))
    if value == nil or value < 0 or value % 1 ~= 0 then
        return nil
    end
    return value
end

local function main()
    local disk = sys_prtinfo()
    if disk == nil then
        print("error: no primary disk found to partition.")
        return
    end

    print("found primary disk: " .. disk.name)
    io.write("checking for partition scheme: ")
    if disk.scheme == "gpt" then
        print("found guid partition table")
    elseif disk.scheme == "mbr" then
        print("found good ol' MBR.")
    else
        print("no partition table (raw, like republicans like their meat).")
    end

    print("\n[1] create GUID partition table")
    print("[2] create the master boot record")
    print("[3] keep current scheme and just nuke the partitions")
    io.write("choose an option: ")
    io.flush()
    local choice = tonumber(io.read("*l"))
    if choice ~= 1 and choice ~= 2 and choice ~= 3 then
        print("invalid option.")
        return
    end

    local scheme_ok, scheme_message = sys_prtscheme(choice)
    if not scheme_ok then
        print("failed: " .. tostring(scheme_message))
        return
    end
    if choice ~= 3 then
        print(scheme_message)
    end

    print("\n--- partition sizing ---")
    local start_sector = read_integer("enter start sector (standard default is 2048 for some reason): ")
    if start_sector == nil then
        print("invalid number.")
        return
    end

    local sector_count = read_integer("enter sector count (example: figure it out yourself): ")
    if sector_count == nil or sector_count == 0 then
        print("invalid number.")
        return
    end

    if start_sector >= disk.block_count then
        print(string.format("start sector %.0f is past the end of the disk (%.0f sectors total).", start_sector, disk.block_count))
        return
    end

    local available = disk.block_count - start_sector
    if sector_count > available then
        local disk_mib = math.floor(disk.block_count * disk.block_size / (1024 * 1024))
        print(string.format(
            "that's %.0f sectors, but only %.0f are free after sector %.0f on a %.0f-sector (%d MiB) disk.",
            sector_count,
            available,
            start_sector,
            disk.block_count,
            disk_mib))
        print("pick a smaller sector count, or get a bigger disk.")
        return
    end

    print("\nwriting partition layout...")
    local success, result = sys_partition(start_sector, sector_count)
    if not success then
        print(result)
        return
    end

    print("rescanning partition tables to alert kernel... (hey, this gives ioctl() vibes) done.")
    print(result)
end

local ok, err = pcall(main)
if not ok then
    print("mkprt: " .. tostring(err))
end