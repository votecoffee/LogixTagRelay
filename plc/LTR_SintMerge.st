// PLC-first three-way merge. LastSent and Received are never modified.
Error := 0;
Status := 0;
PlcChangedBytes := 0;
ReceivedChangedBytes := 0;
ConflictBytes := 0;
AppliedBytes := 0;
SIZE(LastSent, 0, LastSize);
SIZE(Current, 0, CurrentSize);
SIZE(Received, 0, ReceivedSize);

IF (LastSize <> CurrentSize) OR (ReceivedSize <> CurrentSize) OR (CurrentSize <= 0) THEN
    Error := 1;
    Status := 1;
ELSE
    FOR Index := 0 TO CurrentSize - 1 DO
        IF Current[Index] <> LastSent[Index] THEN
            PlcChangedBytes := PlcChangedBytes + 1;
        END_IF;
        IF Received[Index] <> LastSent[Index] THEN
            ReceivedChangedBytes := ReceivedChangedBytes + 1;
        END_IF;
        IF (Current[Index] <> LastSent[Index]) AND (Received[Index] <> LastSent[Index]) AND (Current[Index] <> Received[Index]) THEN
            ConflictBytes := ConflictBytes + 1;
        END_IF;
        IF Current[Index] = LastSent[Index] THEN
            IF Current[Index] <> Received[Index] THEN
                AppliedBytes := AppliedBytes + 1;
            END_IF;
            Current[Index] := Received[Index];
        END_IF;
    END_FOR;
END_IF;
EnableOut := NOT Error;
