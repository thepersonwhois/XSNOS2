local function read_line(prompt)
    io.write(prompt)
    io.flush()
    return io.read("*l")
end

local function main()
    print("XSNOS installer")
    print("this installer erases every sector on the selected disk.")
    print("the running installer disk is never a valid target, if you try you are very stupid")
    print("\ndetecting block devices...")

    local disks = sys_installer_disks()
    if disks == nil then
        print("cannot identify the disk containing the running installer; refusing to continue.")
        return
    end

    local available = {}
    for _, disk in ipairs(disks) do
        if disk.is_boot then
            print(string.format("[boot] %s  %.2f GiB (protected)", disk.name, disk.size_gib))
        else
            available[#available + 1] = disk
            print(string.format("[%d] %s  %.2f GiB", #available, disk.name, disk.size_gib))
        end
    end

    if #available == 0 then
        print("no installable disks found. attach a separate target disk and reboot the installer.")
        return
    end

    local selection = tonumber(read_line("\nselect disk: "))
    if selection == nil or selection % 1 ~= 0 or selection < 1 or selection > #available then
        print("invalid disk selection.")
        return
    end

    local target = available[selection]
    print("\ntarget: " .. target.name .. string.format(" (%.2f GiB)", target.size_gib))
    print("layout: GPT, first partition EFI System, FAT32; remaining space split into max-size FAT32 partitions.")
    print("all sectors will be overwritten. this will not be fast. or maybe it will.")
    print("files from the installer volume will be copied to the first partition.")

    local confirmation = read_line("type the disk name exactly to erase and install: ")
    if confirmation ~= target.name then
        print("confirmation did not match; nothing was changed.")
        return
    end

    print("\ninstallation is happening. yippe.")
    local ok, message = sys_install(target.name)
    if ok then
        print(message)
    else
        print("installer stopped: " .. tostring(message))
        print("this is not green.")
    end
end

local ok, err = pcall(main)
if not ok then
    print("installer: " .. tostring(err))
end