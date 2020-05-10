# SparseLevelling

Post-processing *script* to use auto bed levelling only on the actually used areas of the print bed and thus potentially speed up bed levelling significantly especially for small objects.

## Assumptions and limitations

- Windows (uses GDI+ for convenience)
- PrusaSlicer
- Relative Extrusion (because of the way the relevant moves are determined)
- Marlin
- UBL (unified bed levelling)
- bed origin is in the front left and has coordinates `(0,0)`
- start gcode ends with `; End Start G-code`
- end gcode begins with `; Begin End G-code`
- there is a pair of lines containing `; BEGIN LEVEL` and `; END LEVEL`. These lines and anything in between is replaced by the output of this script.
