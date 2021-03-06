﻿module Asmb.AST.Parsing

open FParsec
open Asmb


type 'a Parser = Parser<'a,unit>


let keywords = ["func"; "byte"; "word"; "dword"; "return"; "if"; "else"; "while"; "pushpop"; "push#"; "as"]

let psize: _ Parser = 
    choiceL [
        stringReturn "void" Void
        stringReturn "byte" Byte
        stringReturn "word" Word
        stringReturn "dword" DWord
    ] "size"
    .>> spaces

let pliteral: Literal Parser = 
    choiceL [
        numberLiteral (NumberLiteralOptions.AllowBinary 
                       ||| NumberLiteralOptions.AllowHexadecimal) "number"
        |>> fun a -> UInt (uint32 a.String)
        pchar '\'' >>. anyChar .>> pchar '\'' |>> Char
    ] "literal"
    .>> spaces

let pidentifier : string Parser =
    let isIdentifierFirstChar c = isLetter c || c = '_'
    let isIdentifierChar c = isLetter c || isDigit c || c = '_'
    
    //>>= fun s -> if not <| List.contains s keywords then preturn s else fail <| sprintf "Expected an identifier. Found the keyword '%s'" s
    notFollowedByL (choice (Seq.map pstring keywords)) "keyword"
    >>. many1Satisfy2L isIdentifierFirstChar isIdentifierChar "identifier" .>> spaces

let pexpr, piexpr = createParserForwardedToRef () 
//let pidOrIndex = 
//    (pidentifier |>> Variable)
//    <|> (pchar '[' >>. opt psize .>> spaces .>>. pexpr .>> spaces .>> pchar ']' |>> fun (s,e) -> Index (e,Option.defaultValue Byte s))

let sepBy1Save (p:'a Parser) (sep:'b Parser) : _ Parser = p .>>. many (sep .>>. p) 
    
/// Also parser whitespace before and after
let patom = 
    choiceL [
        pliteral |>> Constent
        //pstring "offset " >>. pidentifier |>> Offset 
        pchar '(' >>. pexpr .>> pchar ')'
        parse {
            let! name = pidentifier
            let! param = 
                pchar '(' >>. sepBy pexpr (pchar ',' .>> spaces) .>> pchar ')'
                |> opt
            return match param with None -> Variable name | Some param -> Call(name, param)
        }
    ] "expression atom"
    .>> spaces 

let pterm = 
    parse {
        let! operators = many (choiceL [
            stringReturn "&" PointerOf
            stringReturn "*" PointerVal
            stringReturn "!" Not
        ] "unary operator" .>> spaces) 
        let! atom = patom
        return List.foldBack(fun o e -> UOperation(o, e)) operators atom
    } .>> spaces <?> "expression term"

do piexpr :=
    let binary sep p = 
        sepBy1Save p (sep .>> spaces)
        |>> fun (fst, rest) -> List.fold (fun e1 (o, e2) -> BiOperation(o,e1,e2)) fst rest
    pterm
    |> binary (choice [stringReturn "*" Mul; stringReturn "/" Div; stringReturn "%" Mod])
    |> binary (choice [stringReturn "+" Add; stringReturn "-" Sub])
    |> binary (choice [stringReturn "=" EQ; stringReturn "!=" NEQ; stringReturn ">=" NLesser; stringReturn "<=" NGreater; stringReturn "<" Lesser; stringReturn ">" Greater])
    |> binary (choice [stringReturn "or" Or; stringReturn "and" And])
    .>>. opt (pstring "as" >>. spaces >>. psize .>> spaces)
    |>> function expr, None -> expr | expr, Some size -> Convert(expr, size)
    <??> "expression"


let pblock, piblock = createParserForwardedToRef()

let pcomment: _ Parser = 
    choice [
        pstring "//" >>. restOfLine true
        pstring "/*" >>. manyCharsTill anyChar (pstring "*/")
    ]

let rec pif = 
    let pif, pifImpl = createParserForwardedToRef()
    do pifImpl := 
        let pelse = skipString "else" >>. spaces >>. ((pif |>> fun s -> Block(None, [s])) <|> pblock)
        pstring "if" >>. spaces >>. tuple3 pexpr pblock (opt pelse) |>> IfElse
    pif

let pstatement = 
    choice [
        //  Statements with keywords or symbols at the beginning are easier to parse. Parse them first.
        pstring "return" >>. spaces >>. opt pexpr |>> Return    
        pstring "push#" >>. spaces >>. pexpr |>> UnsafePush
        pstring "pop#" >>. spaces >>. pexpr |>> UnsafePop
        pstring "###" >>. spaces >>. manyCharsTill anyChar (pstring "###") |>> fun x -> x.Replace("\r\n", "\n").Split('\n') |> Seq.map(fun s -> s.Trim()) |> Seq.where ((<>) "") |> Array.ofSeq |> NativeAssemblyLines
        pstring "pushpop" >>. spaces >>. sepBy1 pterm (pchar ',' >>. spaces) .>>. pblock |>> Pushpop
        pif
        pstring "while" >>. spaces >>. pexpr .>>. pblock |>> While
        pcomment |>> Comment
        //  Statements with ambiguaty at the start should be parsed carfully using .>>.? operators
        pterm .>>? pstring "<-" .>> spaces .>>. pexpr |>> Assign
        pidentifier .>>.? psize .>>. (opt ((skipChar '=' <|> skipString "<-") >>. spaces >>. pexpr)) |>> fun ((a,b),c) -> StackDeclare(a,b,c)
        pexpr |>> SideEffect
        //pidentifier .>>.? (stringReturn "++" Increment <|> stringReturn "--" Decrement) |>> fun (a,f) -> f a
    ]
    .>> spaces

do piblock :=
    let pline = 
        parse {
            let! statement = pstatement
            do! match statement with
                | Comment _ | IfElse _ | While _ | Pushpop _ | NativeAssemblyLines _ -> preturn ()
                | _ -> skipChar ';'
            return statement
        } .>> spaces
    pchar '{' >>. spaces >>. opt pcomment .>> spaces .>>. many pline .>> pchar '}' .>> spaces |>> Block


let pproc = 
    let parameter = pidentifier .>>. psize
    pstring "func" >>. spaces >>. psize .>>. pidentifier 
    .>> pchar '(' .>> spaces .>>. sepBy parameter (pchar ',' >>. spaces) .>> pchar ')' .>> spaces
    .>>. pblock
    |>> fun (((ret,id),param),s) -> {Name = id; Body = s; Parameters = param; RetSize = ret}
        
let pprogram = 
    let pglobalDeclaration: GlobalDeclaration Parser = 
        choice [
            //  C Array
            pchar '{' >>. spaces >>. sepEndBy1 pliteral (pchar ',' .>> spaces) .>> pchar '}'
            |>> (Array.ofList >> GlobalArray)
            //  C String
            many1 (pchar '"' >>. manyCharsTill anyChar (pchar '"') .>> spaces) .>> pchar ';'
            |>> (String.concat "" >> GlobalString)
            //  x dup literal
            pint32 .>>? spaces .>>? pstring "dup" .>> spaces .>>. pliteral .>> pchar ';'
            |>> GlobalDuplicates
            //  literal
            pliteral .>> pchar ';'
            |>> GlobalVariable
        ]
        .>> spaces
    let variableDeclaration = 
        tuple3 pidentifier psize (pstring "=" >>. spaces >>. pglobalDeclaration)

    spaces >>. many variableDeclaration .>> many (pcomment .>> spaces) .>>. many1 (pproc .>> many (pcomment .>> spaces)).>> eof
    |>> fun (vars,procs) -> {ProgFunctions = procs; ProgVariables = List.map (fun (id,size,v) -> id, size, v) vars}
