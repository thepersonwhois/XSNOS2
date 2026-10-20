<h1>XSNOS 2 (XSNOS NX)</h1>
<p>XSNOS 2 is an operating system that is definitely not very poorly made.</p>

<h2>building</h2>
<p>go into project root and run <code>dotnet publish -c Debug -r linux-x64 -o output-x64 --verbosity minimal</code></p>

<h2>usage</h2>
<p>type hlpcm for help.</p>

<h2>making userspace programs</h2>
<p>use txted. It's just lua 5.5. assume anything not listed below in not working works, unless proven otherwise. file an issue if something doesn't work.</p>
<p>The non-stock lua syscalls are</p>
<ul>
  <li>sys_green() - Makes your text GREEN</li>
  <li>sys_scrh() - Gets the screen height</li>
  <li>sys_scrw() - Gets the screen width</li>
  <li>sys_ver() - Gets the version of XSNOS you're working with.</li>
  <li>sys_white() - Undoes sys_green(), as sys_green() is permanent and persists when your Lua program closes.</li>
</ul>
<p>the non-working lua functions are</p>
<ul>
  <li>io.popen()</li>
  <li>os.tmpname()</li>
  <li>os.getenv()</li>
</ul>
