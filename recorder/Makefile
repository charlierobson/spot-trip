SWIFTC = swiftc
FLAGS  = -O -framework CoreAudio -framework AudioToolbox -framework AVFoundation -framework Foundation

.PHONY: all clean

all: recorder

recorder: recorder.swift
	$(SWIFTC) $(FLAGS) $< -o $@

clean:
	rm -f recorder
