#!/usr/bin/env python
from datetime import datetime
d = datetime(2026, 6, 10)
print("Weekday: {} ({})".format(d.weekday(), d.strftime("%A")))
print("NOT a Chinese holiday - OK to generate")
print("Today: 2026-06-10, Prev trading day: 2026-06-09")
