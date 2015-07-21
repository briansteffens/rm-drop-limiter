#rm-drop-limiter

This is a program that adds randomness and configuration flexibility to the
unique/sunset drop system on Redmoon servers. This is done by periodically
incrementing the ItemCountLimit column in the tblSpecialItemLimit1 table of 
the Redmoon database.

Some of the benefits this can provide are:
- Harder (or impossible with proper configuration?) for players to reliably 
  predict drop times and locations.
- Extreme rarity levels of items (like on Diosa) are possible without it being 
  completely impossible to find them as drops. For example items can have a
  percentage chance to drop over the course of a number of years even.
- On standard Redmoon servers, maps that aren't cleared for a long time
  "fill up" with drops so that the next time they're cleared they drop
  everything all at once. After that there's no point in staying on the map
  so people just clear and move on, even logging off if all maps are cleared.
  rm-drop-limiter can prevent this (for unis/suns/duras) with the right 
  configuration.
- Drop rates could be scaled up or down in realtime according to things like
  server activity levels or events. This can be done without the game server
  restarting.
- Flexible engine keeps droprate percentages consistent no matter how often the
  EXE is run or how much server downtime there is. Restarts etc have no effect
  on the likelihood for items to drop.
- Setups that would be impossible otherwise are enabled by rm-drop-limiter.
  For example it could be possible for any unique to drop on any map while
  still keeping them exceedingly rare.

<i>
Note: installation and configuration may be a bit rough depending on the kinds
of software you're used to. rm-drop-limiter currently uses the command line and 
text config files. If there is any interest in this I would probably take the 
time to write a GUI and simplify usage. I would also be open to custom 
suggestions or helping with installations, particularly if you run one of the 
major private servers.
</i>


###Prerequisites

- Get a Redmoon server running. This was developed against 3.8legacy and 4.4
  but most versions should work.
- Install the [.NET Framework 4.0 redistributable](http://www.microsoft.com/en-us/download/details.aspx?id=17851)
  on the system that will host rm-drop-limiter. Note that this does not need
  to be the game server: any system that can run .NET binaries and access the
  database over TCP/IP can host rm-drop-limiter. This can also be a non-Windows
  system using Mono (in fact this project was developed on Linux with Mono).



###Install rm-drop-limiter

You can build the project from source if you want, or just download the
binary [here](https://coldplace.net/redmoon/rm-drop-limiter.zip). Unzip the 
following files:
```
rm-drop-limiter.exe
rm-drop-limiter.conf
rm-drop-limiter.drops
```
They can be installed anywhere as long as they're all in the same folder.



###Redmoon config changes

By default, the Redmoon server spawn files `Data/Mop/Mop00###.rsm` and
`Data/Mop/MopSpc.rsm` have high respawn timers for the mobs that drop sunsets
and uniques. While it won't break anything to leave them this way, it won't 
work as intuitively with rm-drop-limiter. Since rm-drop-limiter will be 
controlling what is allowed to drop at the database level, it depends on mobs
being spawned in-game that can drop the item to make the drop possible. Leaving
these respawn timers high will create more of a disconnect between
rm-drop-limiter deciding to increase the pool of available drops and a drop 
becoming possible in-game.

It's up to you how you want things to work, but most servers will need at 
least some adjustments to get the desired level of granularity. You can use 
the Redmoon GM Tool to change the respawn times for mobs that drop 
uniques/sunsets or just edit the files by hand.

My recommendation is to lower the respawn times of unique/sunset-dropping
mobs to around the same speed as normal mobs, which allows rm-drop-limiter to
work as intuitively as possible.



###Database patch

There is an optional database patch that fixes what can be a fairly large
problem. Consider the following scenario: there are 100 duras owned by players
(in `tblSpecialItem1`) in the game. In rm-drop-limiter.conf, durabilities have
a max pool size of 5, so there should only ever be 5 ready to drop at once.
The durability limit is correctly set to 105 (`tblSpecialItemLimit1`) by 
rm-drop-limiter. Now say someone buys an item from the sah1 trader for 20
duras, which get deleted from `tblSpecialItem1`. The durability pool size is now
left incorrectly at 25 (105-80), meaning that until rm-drop-limiter runs again
and fixes the pool size, 5x the configured maximum number of duras are ready
to drop.

The database patch installs a trigger `RMT_DROPLIMITER_DECREMENT_LIMIT` which
decrements the associated row in `tblSpecialItemLimit1` whenever a special item
is deleted from `tblSpecialItem1`. This keeps the pool size consistent and
prevents situations like these from causing extra drops.

In the default configuration, you will be prompted to install the database
patch automatically by `rm-drop-limiter.exe` if it isn't already installed. For 
other options, including uninstallation, see `rm-drop-limiter.conf`.



###rm-drop-limiter configuration

rm-drop-limiter is configured by customizing the following files:
```
rm-drop-limiter.conf   # database credentials, logging, etc
rm-drop-limiter.drops  # drop rates and limits
```
Both files are commented and contain examples. Make sure to read both and
make changes to suit your environment before continuing.



###Running rm-drop-limiter

Once everything is configured, run `rm-drop-limiter.exe` in the console so
you can see the log output. This should create a file called 
`rm-drop-limiter.state` and then exit without processing any drops.

The next time `rm-drop-limiter.exe` is run, it will use the state file to
determine the number of seconds since the previous run, and then work its way
forward through time, simulating dice throws for each configured time period 
(or fraction of a time period).

The log can be viewed on the console or in a file by changing
`rm-drop-limiter.conf`. The log shows every simulated dice throw and can help
to explain how the program works.

Run `rm-drop-limiter.exe -h` for more information on the command line interface.



###Automating rm-drop-limiter.exe execution

After being setup and configured, `rm-drop-limiter.exe` should be re-run
continually every 1-20 minutes or so. This frequency can be pretty much 
anything with caveats at the extremes:
- Since this program executes SQL code against your database, a frequency of
  a matter of seconds could be detrimental to performance. How fast you can
  run it will depend on your hardware and configuration.
- Waiting too long between executions can limit the granularity of the 
  `.drops` file. For example, configuring your drop rates by the hour but
  only running `rm-drop-limiter.exe` once a day would cause nothing to drop all
  day until `rm-drop-limiter.exe` runs, catches up, and spawns a whole day's
  worth of items all at once.

`rm-drop-limiter.exe` is a console application, so it can be automated in a
number of ways but as a starting point on Windows, see
[Windows Task Scheduler](http://google.com/search?q=windows+task+scheduler).


